using System.Security.Cryptography;
using System.Text.Json;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;
using FlashNext.Core.Services;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Infrastructure.Windows.Hardware;

public sealed class VulkanProbeClient(IProcessRunner processRunner, PlatformPaths paths)
{
    public const int DefaultTimeoutSeconds = 15;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    public string? FindExecutable()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public IEnumerable<string> EnumerateCandidates()
    {
        yield return Path.Combine(paths.ApplicationDirectory, "FlashNext.VulkanProbe.exe");
        yield return Path.Combine(paths.ApplicationDirectory, "bootstrap", "FlashNext.VulkanProbe.exe");

        DirectoryInfo? current = new(paths.ApplicationDirectory);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "FlashNext.VulkanProbe.exe");
            yield return Path.Combine(current.FullName, "bootstrap", "FlashNext.VulkanProbe.exe");
            if (File.Exists(Path.Combine(current.FullName, "FlashNext.sln"))) break;
            current = current.Parent;
        }
    }

    public void VerifyBootstrapHash(string executablePath)
    {
        string? lockPath = FindBootstrapLock(executablePath);
        if (lockPath is null) return;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockPath));
        JsonElement root = document.RootElement;
        string expectedHash = root.GetProperty("sha256").GetString() ?? string.Empty;
        long expectedBytes = root.GetProperty("bytes").GetInt64();
        FileInfo info = new(executablePath);
        if (info.Length != expectedBytes)
        {
            throw new InvalidDataException($"The bootstrap Vulkan probe size is {info.Length} bytes; bootstrap.lock.json requires {expectedBytes} bytes. Restore the original source package and rerun install.cmd.");
        }
        using FileStream stream = File.OpenRead(executablePath);
        byte[] hash = SHA256.HashData(stream);
        string actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The bootstrap Vulkan probe SHA-256 is {actual}; bootstrap.lock.json requires {expectedHash}. Restore the original source package and rerun install.cmd.");
        }
    }

    public async Task<VulkanProbeRun> RunAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        string? executable = FindExecutable();
        if (executable is null)
        {
            throw new FileNotFoundException("FlashNext.VulkanProbe.exe was not found next to the manager or in bootstrap/. Rebuild the probe and rerun install.cmd.");
        }

        try
        {
            VerifyBootstrapHash(executable);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"The bootstrap Vulkan probe could not be verified: {exception.Message}", exception);
        }

        ProcessResult result = await processRunner.RunAsync(
            new ProcessSpec(executable, ["--timeout-ms", Math.Clamp((int)timeout.TotalMilliseconds, 250, 120000).ToString()], Path.GetDirectoryName(executable)),
            timeout,
            cancellationToken).ConfigureAwait(false);

        string combined = result.StandardOutput + Environment.NewLine + result.StandardError;
        return new VulkanProbeRun
        {
            Executable = executable,
            Process = result,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            CombinedText = combined.Trim(),
            TimedOut = result.TimedOut,
            ExitCode = result.ExitCode
        };
    }

    private static string? FindBootstrapLock(string executablePath)
    {
        DirectoryInfo? current = new(Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "manifests", "bootstrap.lock.json");
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(current.FullName, "FlashNext.sln")))
            {
                return File.Exists(candidate) ? candidate : null;
            }
            current = current.Parent;
        }
        return null;
    }
}

public sealed class VulkanProbeRun
{
    public string Executable { get; set; } = string.Empty;
    public ProcessResult Process { get; set; } = new();
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public string CombinedText { get; set; } = string.Empty;
    public bool TimedOut { get; set; }
    public int ExitCode { get; set; }

    public string DiagnosticTail
    {
        get
        {
            string text = CombinedText;
            return text.Length <= 8000 ? text : text[^8000..];
        }
    }
}
