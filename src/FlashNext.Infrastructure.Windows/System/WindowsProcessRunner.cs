using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;
using FlashNext.Infrastructure.Windows.Runtime;

namespace FlashNext.Infrastructure.Windows.System;

public sealed class WindowsProcessRunner : IProcessRunner
{
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "A positive timeout is required. Indefinite process waits are not permitted.");

        ProcessStartInfo start = new()
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = spec.CaptureOutput,
            RedirectStandardError = spec.CaptureOutput,
            CreateNoWindow = spec.CreateNoWindow
        };
        if (spec.CaptureOutput)
        {
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (string argument in spec.Arguments) start.ArgumentList.Add(argument);
        if (spec.Environment is not null)
        {
            foreach ((string key, string? value) in spec.Environment) start.Environment[key] = value;
        }

        using Process process = new() { StartInfo = start, EnableRaisingEvents = true };
        WindowsJobObject? job = null;
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException($"Could not start '{spec.FileName}'.");
        int processId = process.Id;
        try
        {
            job = new WindowsJobObject("FlashNext-Proc-" + processId.ToString() + "-" + Guid.NewGuid().ToString("N"));
            job.Add(process);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            job?.Dispose();
            job = null;
        }

        Task<string> stdoutTask = spec.CaptureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        Task<string> stderrTask = spec.CaptureOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        bool timedOut = false;
        bool terminationSucceeded = true;

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            terminationSucceeded = await TerminateTreeAsync(process, job).ConfigureAwait(false);
            await DrainOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (!timedOut) cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            stopwatch.Stop();
            job?.Dispose();
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        int exitCode = timedOut ? -1 : GetExitCode(process);
        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            Elapsed = stopwatch.Elapsed,
            TimedOut = timedOut,
            ProcessId = processId,
            TerminationSucceeded = terminationSucceeded,
            FileName = spec.FileName
        };
    }

    private static async Task<bool> TerminateTreeAsync(Process process, WindowsJobObject? job)
    {
        bool succeeded = true;
        try
        {
            if (!process.HasExited)
            {
                job?.Terminate(1);
                try
                {
                    using CancellationTokenSource grace = new(KillGrace);
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { succeeded = false; }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            succeeded = false;
        }

        try
        {
            using CancellationTokenSource wait = new(KillWait);
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { succeeded = false; }
            succeeded = process.HasExited && succeeded;
        }
        catch (InvalidOperationException)
        {
        }
        return succeeded && (process.HasExited || GetHasExited(process));
    }

    private static async Task DrainOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            using CancellationTokenSource drain = new(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(drain.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
        }
    }

    private static int GetExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private static bool GetHasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
}
