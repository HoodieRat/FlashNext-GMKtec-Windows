using System.Text.RegularExpressions;

namespace FlashNext.InstallationTests;

public sealed class ProcessAndProbeContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void NoFullNoArgumentVulkanInfoCommandRemains()
    {
        string[] files =
        [
            Path.Combine(Root, "scripts", "Test-BootstrapHardware.ps1"),
            Path.Combine(Root, "scripts", "Install-FlashNext.ps1"),
            Path.Combine(Root, "scripts", "Common.ps1"),
            Path.Combine(Root, "src", "FlashNext.Infrastructure.Windows", "Hardware", "WindowsHardwareProbe.cs")
        ];
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("new ProcessSpec(vulkanInfo, []", text, StringComparison.Ordinal);
            Assert.False(Regex.IsMatch(text, @"vulkaninfo(?:SDK)?\.exe[\s\S]{0,200}Arguments @\(\)", RegexOptions.IgnoreCase), file);
            Assert.False(Regex.IsMatch(text, @"Invoke-FlashNextNative[\s\S]{0,200}vulkaninfo[\s\S]{0,80}Arguments @\(\)", RegexOptions.IgnoreCase), file);
        }

        string bootstrap = File.ReadAllText(Path.Combine(Root, "scripts", "Test-BootstrapHardware.ps1"));
        string probe = File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Infrastructure.Windows", "Hardware", "WindowsHardwareProbe.cs"));
        Assert.Contains("FlashNext.VulkanProbe.exe", bootstrap, StringComparison.Ordinal);
        Assert.Contains("direct-vulkan-memory-probe", bootstrap, StringComparison.Ordinal);
        Assert.Contains("VulkanProbeClient", probe, StringComparison.Ordinal);
        Assert.Contains("VulkanProbeEvaluator", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseVulkan", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeInvocationRequiresTimeoutsAndProcessTreeTermination()
    {
        string common = File.ReadAllText(Path.Combine(Root, "scripts", "Common.ps1"));
        Assert.Contains("TimeoutSeconds", common, StringComparison.Ordinal);
        Assert.Contains("Stop-FlashNextProcessTree", common, StringComparison.Ordinal);
        Assert.Contains("TerminateJobObject", common, StringComparison.Ordinal);
        Assert.Contains("HeartbeatSeconds", common, StringComparison.Ordinal);
        Assert.Contains("Get-FlashNextRedactedText", common, StringComparison.Ordinal);
        Assert.Contains("Convert-FlashNextInstallState", common, StringComparison.Ordinal);
        Assert.DoesNotContain("$process.WaitForExit()", common, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 200", common, StringComparison.Ordinal);
        Assert.Contains("System.TimeoutException", common, StringComparison.Ordinal);
        Assert.Contains("terminationSucceeded", common, StringComparison.Ordinal);
    }

    [Fact]
    public void StateMigrationFrom104IsImplemented()
    {
        string common = File.ReadAllText(Path.Combine(Root, "scripts", "Common.ps1"));
        string installer = File.ReadAllText(Path.Combine(Root, "scripts", "Install-FlashNext.ps1"));
        Assert.Contains("Convert-FlashNextInstallState", installer, StringComparison.Ordinal);
        Assert.Contains("$recorded -eq '1.0.4' -and $ProjectRevision -eq '1.0.5'", common, StringComparison.Ordinal);
        Assert.Contains("vulkanHardwareResultInvalidated", common, StringComparison.Ordinal);
        Assert.Contains("direct-vulkan-memory-probe", installer, StringComparison.Ordinal);
        Assert.Contains("projectRevision = '1.0.5'", installer, StringComparison.Ordinal);
        Assert.Contains("@('1.0.4','1.0.5')", installer, StringComparison.Ordinal);
        Assert.Contains("before any dependency installation", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelDownloaderRejectsExecutableRemoteContent()
    {
        string downloader = File.ReadAllText(Path.Combine(Root, "scripts", "model_download.py"));
        Assert.Contains("FORBIDDEN_REMOTE_SUFFIXES", downloader, StringComparison.Ordinal);
        Assert.Contains("assert_downloaded_files_are_safe", downloader, StringComparison.Ordinal);
        Assert.Contains(".exe", downloader, StringComparison.Ordinal);
        Assert.DoesNotContain("trust_remote_code", downloader, StringComparison.Ordinal);
        Assert.Contains("quarantine", downloader, StringComparison.Ordinal);
        Assert.Contains("KeyboardInterrupt", downloader, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiLoopbackAndKeyEnforcementRemainFactoryDefaults()
    {
        string settings = File.ReadAllText(Path.Combine(Root, "config", "factory.settings.json"));
        string arguments = File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Core", "Services", "ServerArgumentBuilder.cs"));
        Assert.Contains("\"host\": \"127.0.0.1\"", settings, StringComparison.Ordinal);
        Assert.Contains("--api-key-file", arguments, StringComparison.Ordinal);
        Assert.Contains("--metrics", arguments, StringComparison.Ordinal);
        Assert.Contains("includePromptOrResponse", File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Core", "Services", "MetricCalculator.cs")) + File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Core", "Services", "MetricsWriter.cs")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootstrapLockManifestExistsWithSha256Field()
    {
        string lockPath = Path.Combine(Root, "manifests", "bootstrap.lock.json");
        Assert.True(File.Exists(lockPath), "manifests/bootstrap.lock.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lockPath));
        string expectedHash = document.RootElement.GetProperty("sha256").GetString() ?? string.Empty;
        long expectedBytes = document.RootElement.GetProperty("bytes").GetInt64();
        string probe = Path.Combine(Root, "bootstrap", "FlashNext.VulkanProbe.exe");
        Assert.True(File.Exists(probe), "bootstrap/FlashNext.VulkanProbe.exe");
        FileInfo info = new(probe);
        Assert.Equal(expectedBytes, info.Length);
        using FileStream stream = File.OpenRead(probe);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
        Assert.Equal(expectedHash, Convert.ToHexString(hash).ToLowerInvariant());
    }

    [Fact]
    public void InterruptedBuildAndModelDownloadRemainResumable()
    {
        string installer = File.ReadAllText(Path.Combine(Root, "scripts", "Install-FlashNext.ps1"));
        string build = File.ReadAllText(Path.Combine(Root, "scripts", "Build-FlashNext.ps1"));
        string downloader = File.ReadAllText(Path.Combine(Root, "scripts", "model_download.py"));
        string runtime = File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Infrastructure.Windows", "Runtime", "RuntimeManager.cs"));
        Assert.Contains("Set-FlashNextInstallStage", installer, StringComparison.Ordinal);
        Assert.Contains("build-cache", build, StringComparison.Ordinal);
        Assert.Contains("Resumable cache data was preserved", downloader, StringComparison.Ordinal);
        Assert.Contains("quarantine", downloader, StringComparison.Ordinal);
        Assert.Contains("previous", runtime, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RollbackAsync", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeTimeoutAndCrashAreCoveredByRunnerTests()
    {
        string tests = File.ReadAllText(Path.Combine(Root, "tests", "FlashNext.IntegrationTests", "ProcessTimeoutTests.cs"));
        Assert.Contains("TimeoutKillsHangingProcessTree", tests, StringComparison.Ordinal);
        Assert.Contains("--spawn-child", tests, StringComparison.Ordinal);
        Assert.Contains("--crash", tests, StringComparison.Ordinal);
        Assert.Contains("CancellationDoesNotReportSuccess", tests, StringComparison.Ordinal);
        Assert.Contains("PathsWithSpacesAndNonAsciiWork", tests, StringComparison.Ordinal);
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
}
