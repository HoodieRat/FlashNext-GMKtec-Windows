using System.Diagnostics;
using FlashNext.Core.Models;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.IntegrationTests;

public sealed class ProcessTimeoutTests
{
    [Fact]
    public async Task TimeoutKillsHangingProcessTree()
    {
        string helper = FindHangHelper();
        WindowsProcessRunner runner = new();
        ProcessResult result = await runner.RunAsync(
            new ProcessSpec(helper, ["--sleep-ms", "60000", "--spawn-child"], Path.GetDirectoryName(helper)),
            TimeSpan.FromSeconds(3));

        Assert.True(result.TimedOut);
        Assert.True(result.ProcessId.HasValue);
        Assert.True(result.TerminationSucceeded);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(20));
        await Task.Delay(500);
        Assert.Empty(Process.GetProcessesByName("FlashNext.TestHangHelper"));
    }

    [Fact]
    public async Task CrashDoesNotHangTheRunner()
    {
        string helper = FindHangHelper();
        WindowsProcessRunner runner = new();
        ProcessResult result = await runner.RunAsync(
            new ProcessSpec(helper, ["--crash"], Path.GetDirectoryName(helper)),
            TimeSpan.FromSeconds(10));
        Assert.False(result.TimedOut);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task PathsWithSpacesAndNonAsciiWork()
    {
        string root = Path.Combine(Path.GetTempPath(), "FlashNext Tests", "vägen " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            WindowsProcessRunner runner = new();
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            ProcessResult result = await runner.RunAsync(
                new ProcessSpec(cmd, ["/c", "echo", "ok-from-space"], root),
                TimeSpan.FromSeconds(15));
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("ok-from-space", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PowerShellNativeTimeoutKillsProcessTreeAndReturnsTimeoutException()
    {
        string helper = FindHangHelper();
        string root = FindRoot();
        string common = Path.Combine(root, "scripts", "Common.ps1");
        string scriptPath = Path.Combine(Path.GetTempPath(), "FlashNext-hang-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, string.Join(Environment.NewLine, [
            "$ErrorActionPreference = 'Stop'",
            $". '{common.Replace("'", "''")}'",
            "try {",
            $"  Invoke-FlashNextNative -FilePath '{helper.Replace("'", "''")}' -Arguments @('--sleep-ms','60000','--spawn-child') -WorkingDirectory '{Path.GetDirectoryName(helper)!.Replace("'", "''")}' -TimeoutSeconds 3 -HeartbeatSeconds 1 -Stage 'hang-injection'",
            "  Write-Output 'UNEXPECTED_SUCCESS'",
            "  exit 0",
            "}",
            "catch [System.TimeoutException] {",
            "  Write-Output 'TIMEOUT_OK'",
            "  if ($_.Exception.Message -notmatch 'pid=') { Write-Output 'MISSING_PID'; exit 18 }",
            "  if ($_.Exception.Message -notmatch 'timeoutSeconds=3') { Write-Output 'MISSING_TIMEOUT'; exit 19 }",
            "  exit 17",
            "}"
        ]));
        try
        {
            string output = RunPowerShellFile(scriptPath, 30000, out int exitCode);
            Assert.True(exitCode == 17, output);
            Assert.Contains("TIMEOUT_OK", output, StringComparison.Ordinal);
            Thread.Sleep(500);
            Assert.Empty(Process.GetProcessesByName("FlashNext.TestHangHelper"));
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void PowerShellStateMigrationFrom104PreservesDependenciesAndInvalidatesVulkanReport()
    {
        string root = FindRoot();
        string common = Path.Combine(root, "scripts", "Common.ps1");
        string work = Path.Combine(Path.GetTempPath(), "FlashNext-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string statePath = Path.Combine(work, "install-state.json");
            File.WriteAllText(statePath, "{\"stage\":\"complete\",\"projectRevision\":\"1.0.4\",\"sourceRoot\":\"C:\\\\FlashNext-GMKtec-Windows\\\\FlashNext-GMKtec-Windows\"}");
            string script = string.Join(Environment.NewLine, [
                "$ErrorActionPreference = 'Stop'",
                $". '{common.Replace("'", "''")}'",
                $"$state = Read-FlashNextJson -Path '{statePath.Replace("'", "''")}'",
                $"$result = Convert-FlashNextInstallState -State $state -ProjectRevision '1.0.5' -SourceRoot '{root.Replace("'", "''")}' -StatePath '{statePath.Replace("'", "''")}'",
                "if (-not $result.Changed) { Write-Output 'NOT_CHANGED'; exit 20 }",
                "if ([string]$result.State.stage -ne 'dependencies-complete') { Write-Output $result.State.stage; exit 21 }",
                "if ([string]$result.State.projectRevision -ne '1.0.5') { exit 22 }",
                "if ([string]$result.State.vulkanProbeKind -ne 'direct-vulkan-memory-probe') { exit 23 }",
                "if (-not [bool]$result.State.vulkanHardwareResultInvalidated) { exit 24 }",
                "Write-Output 'MIGRATION_OK'",
                "exit 17"
            ]);
            string scriptPath = Path.Combine(work, "migrate.ps1");
            File.WriteAllText(scriptPath, script);
            string output = RunPowerShellFile(scriptPath, 30000, out int exitCode);
            Assert.True(exitCode == 17, output);
            Assert.Contains("MIGRATION_OK", output, StringComparison.Ordinal);
            Assert.True(File.Exists(statePath + ".pre-1.0.5.bak"));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task CancellationDoesNotReportSuccess()
    {
        string helper = FindHangHelper();
        WindowsProcessRunner runner = new();
        using CancellationTokenSource source = new();
        source.CancelAfter(400);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new ProcessSpec(helper, ["--sleep-ms", "30000"], Path.GetDirectoryName(helper)),
            TimeSpan.FromSeconds(30),
            source.Token));
    }

    private static string RunPowerShellFile(string scriptPath, int timeoutMilliseconds, out int exitCode)
    {
        ProcessStartInfo start = new()
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new TimeoutException("PowerShell test helper exceeded its timeout.");
        }
        exitCode = process.ExitCode;
        return stdout.Result + stderr.Result;
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FlashNext.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string FindHangHelper()
    {
        string[] names = ["FlashNext.TestHangHelper.exe", "FlashNext.TestHangHelper.dll"];
        foreach (string name in names)
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory, "FlashNext.TestHangHelper.exe", SearchOption.AllDirectories))
        {
            return file;
        }
        throw new FileNotFoundException("FlashNext.TestHangHelper.exe was not copied to the test output directory.");
    }
}
