using System.Text.Json;

namespace FlashNext.InstallationTests;

public sealed class RepositoryContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void RequiredRepositoryTreeExists()
    {
        string[] required = ["install.cmd", "uninstall.cmd", "run.cmd", "diagnose-install.cmd", "FlashNext.sln", "README.md", "THIRD_PARTY_NOTICES.md", "config/factory.settings.json", "config/settings.schema.json", "manifests/dependencies.lock.json", "manifests/runtime.lock.json", "manifests/model.lock.json", "manifests/bootstrap.lock.json", "patches/llama.cpp/0007-halo-compact-shared-mtp-sidecar.patch", "patches/llama.cpp/0008-halo-mtp-request-state-lifecycle.patch", "bootstrap/FlashNext.VulkanProbe.exe", "scripts/Install-FlashNext.ps1", "scripts/Dependency-Probes.ps1", "scripts/Test-BootstrapHardware.ps1", "scripts/Install-Dependencies.ps1", "scripts/Build-FlashNext.ps1", "scripts/Install-MachineComponents.ps1", "scripts/Get-InstallDiagnostics.ps1", "scripts/Uninstall-FlashNext.ps1", "scripts/Resume-Install.ps1", "scripts/Activate-Runtime.ps1", "scripts/Set-LanMode.ps1", "scripts/model_download.py", "scripts/verify_repository.py", "scripts/Run-InferenceValidation.ps1", "scripts/Run-CorrectnessValidation.ps1", "scripts/inference_validation_client.py", "src/FlashNext.Manager/FlashNext.Manager.csproj", "src/FlashNext.Dashboard/FlashNext.Dashboard.csproj", "src/FlashNext.Core/FlashNext.Core.csproj", "src/FlashNext.Infrastructure.Windows/FlashNext.Infrastructure.Windows.csproj", "src/FlashNext.VulkanProbe/FlashNext.VulkanProbe.csproj", "integrations/openai-compatible.md", "integrations/csharp/Program.cs", "integrations/csharp/Control.cs", "integrations/python/client.py", "integrations/python/control.py", "integrations/node/client.mjs", "integrations/opencode/opencode.jsonc", "licenses/llama.cpp-MIT.txt", "licenses/Qwen-Community-License-1.0.txt"];
        foreach (string relative in required) Assert.True(File.Exists(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar))), relative);
    }

    [Fact]
    public void RuntimeAndModelAreImmutableAndComplete()
    {
        using JsonDocument runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifests", "runtime.lock.json")));
        Assert.Equal("7449a0fe9710ab584c5f9a6d25e7a31eea2708b8", runtime.RootElement.GetProperty("commit").GetString());
        Assert.Equal("2b32b2e114a1b96523bdf20fdc469cf47fb63adf", runtime.RootElement.GetProperty("tree").GetString());
        JsonElement[] runtimePatches = runtime.RootElement.GetProperty("patches").EnumerateArray().ToArray();
        Assert.Equal(2, runtimePatches.Length);
        Assert.Contains(runtimePatches, item => item.GetProperty("path").GetString() == "patches/llama.cpp/0007-halo-compact-shared-mtp-sidecar.patch" && item.GetProperty("sha256").GetString()!.Equals("efe66ec394731ae23d068de5bc5f3e610246124e1cf08c2eaeebdcc0e2cd37a5", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(runtimePatches, item => item.GetProperty("path").GetString() == "patches/llama.cpp/0008-halo-mtp-request-state-lifecycle.patch" && item.GetProperty("sha256").GetString() == "c3e8e593abfce0845bbb0e077ab59db555272fe371022f7c0db740586063be93");
        using JsonDocument model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifests", "model.lock.json")));
        Assert.Equal("unsloth/Qwen3.8-Flash-Next-GGUF", model.RootElement.GetProperty("repository").GetString());
        Assert.Equal("38bb39ee97821de2c9009abb7e93950eec396e66", model.RootElement.GetProperty("revision").GetString());
        JsonElement files = model.RootElement.GetProperty("files");
        Assert.Equal(6, files.GetArrayLength());
        Assert.Equal(4, files.EnumerateArray().Count(item => item.GetProperty("kind").GetString() == "target"));
        Assert.Single(files.EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "mtp"));
        JsonElement projector = Assert.Single(files.EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "mmproj"));
        Assert.Equal("mmproj-F16.gguf", projector.GetProperty("path").GetString());
        foreach (JsonElement item in files.EnumerateArray()) Assert.Matches("^[0-9a-f]{64}$", item.GetProperty("sha256").GetString()!);
    }

    [Fact]
    public void FactorySettingsContainRequiredProfilesAndSafeBinding()
    {
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "config", "factory.settings.json")));
        JsonElement profiles = settings.RootElement.GetProperty("profiles");
        foreach (string name in new[] { "coding-balanced", "chat-fast", "benchmark-deterministic", "safe-fallback" }) Assert.True(profiles.TryGetProperty(name, out _), name);
        Assert.Equal("127.0.0.1", settings.RootElement.GetProperty("server").GetProperty("host").GetString());
        Assert.False(settings.RootElement.GetProperty("server").GetProperty("agent").GetBoolean());
        Assert.False(settings.RootElement.GetProperty("server").GetProperty("webUi").GetBoolean());
        Assert.True(settings.RootElement.GetProperty("server").GetProperty("metrics").GetBoolean());
        Assert.True(settings.RootElement.GetProperty("server").GetProperty("jinja").GetBoolean());
    }

    [Fact]
    public void ReinstallPublishesCurrentApplicationBeforeInitializingOldManager()
    {
        string installer = File.ReadAllText(Path.Combine(Root, "scripts", "Install-FlashNext.ps1"));
        string updater = File.ReadAllText(Path.Combine(Root, "scripts", "Publish-AppUpdate.ps1"));
        int publish = installer.IndexOf("scripts\\Publish-AppUpdate.ps1", StringComparison.Ordinal);
        int initialize = installer.IndexOf("-Arguments @('--initialize')", StringComparison.Ordinal);
        Assert.True(publish >= 0 && publish < initialize);
        Assert.Contains("$samePins", installer);
        Assert.Contains("'FlashNext.Manager','FlashNext.Dashboard'", updater);
        Assert.Contains("Update-InstalledApp.ps1", updater);
        Assert.Contains("Installed file did not match the publish", updater);
        Assert.Contains("--no-restore", updater);
        Assert.Contains("New-FlashNextShortcut", updater);
        Assert.DoesNotContain("Invoke-FlashNextElevated", updater);
        string launcher = File.ReadAllText(Path.Combine(Root, "run.cmd"));
        Assert.Contains("if exist \"%LOCALAPPDATA%\\FlashNextManager\\app\\\" set \"APP=", launcher);
    }

    [Fact]
    public void SourceTreeContainsNoIncompleteImplementationMarkersOrUserSpecificPath()
    {
        string[] markers = ["TO" + "DO", "FIX" + "ME", "NotImplemented" + "Exception", "place" + "holder", "dum" + "my", "omitted" + " method"];
        string userPrefix = "C:" + "\\Users\\";
        string[] extensions = [".cs", ".ps1", ".py", ".cmd", ".json", ".md", ".xml", ".props", ".csproj"];
        string[] ignoredDirectoryNames = ["bin", "obj", ".git", "__pycache__", "artifacts"];
        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                     .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .Where(path => !Path.GetRelativePath(Root, path)
                         .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Any(part => ignoredDirectoryNames.Contains(part, StringComparer.OrdinalIgnoreCase))))
        {
            string content = File.ReadAllText(file);
            foreach (string marker in markers) Assert.DoesNotContain(marker, content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(userPrefix, content, StringComparison.OrdinalIgnoreCase);
        }
    }


    [Fact]
    public void HardwarePreflightHandlesWindowsDeviceNamesAndUmaReservations()
    {
        string common = File.ReadAllText(Path.Combine(Root, "scripts", "Common.ps1"));
        string installer = File.ReadAllText(Path.Combine(Root, "scripts", "Install-FlashNext.ps1"));
        string probe = File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Infrastructure.Windows", "Hardware", "WindowsHardwareProbe.cs"));
        string bootstrap = File.ReadAllText(Path.Combine(Root, "scripts", "Test-BootstrapHardware.ps1"));

        Assert.Contains("GetPhysicallyInstalledSystemMemory", common, StringComparison.Ordinal);
        Assert.Contains("Win32_PhysicalMemory", common, StringComparison.Ordinal);
        Assert.Contains("Test-FlashNextGpuTarget", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("-notmatch 'Radeon 8060S'", installer, StringComparison.Ordinal);
        Assert.Contains("HardwareIdentity.IsTargetGpu", probe, StringComparison.Ordinal);
        Assert.Contains("OsVisibleMemoryBytes", probe, StringComparison.Ordinal);
        Assert.Contains("Test-BootstrapHardware.ps1", installer, StringComparison.Ordinal);
        Assert.Contains("DEVICE_LOCAL", bootstrap, StringComparison.Ordinal);
        Assert.Contains("90GB", bootstrap, StringComparison.Ordinal);
        Assert.Contains("FlashNext.VulkanProbe.exe", bootstrap, StringComparison.Ordinal);
        Assert.Contains("direct-vulkan-memory-probe", bootstrap, StringComparison.Ordinal);
        Assert.Contains("VulkanProbeEvaluator", probe, StringComparison.Ordinal);
    }


    [Fact]
    public void InstallerBuildsAsTheUserAndElevatesOnlyVerifiedDeployment()
    {
        string common = File.ReadAllText(Path.Combine(Root, "scripts", "Common.ps1"));
        string installer = File.ReadAllText(Path.Combine(Root, "scripts", "Install-FlashNext.ps1"));
        string dependencies = File.ReadAllText(Path.Combine(Root, "scripts", "Install-Dependencies.ps1"));
        string build = File.ReadAllText(Path.Combine(Root, "scripts", "Build-FlashNext.ps1"));
        string machine = File.ReadAllText(Path.Combine(Root, "scripts", "Install-MachineComponents.ps1"));
        string diagnostics = File.ReadAllText(Path.Combine(Root, "scripts", "Get-InstallDiagnostics.ps1"));

        Assert.Contains("-EncodedCommand", common, StringComparison.Ordinal);
        Assert.Contains("Write-FlashNextOperationResult", common, StringComparison.Ordinal);
        Assert.Contains("source','update','--name','winget", common, StringComparison.Ordinal);
        Assert.DoesNotContain("source','update','--name','winget','--disable-interactivity','--accept-source-agreements", common, StringComparison.Ordinal);
        Assert.DoesNotContain("source','list','--disable-interactivity','--accept-source-agreements", common, StringComparison.Ordinal);
        Assert.Contains("Install-Dependencies.ps1", installer, StringComparison.Ordinal);
        Assert.Contains("Build-FlashNext.ps1", installer, StringComparison.Ordinal);
        Assert.Contains("build-install-result.json", installer, StringComparison.Ordinal);
        Assert.Contains("machine-install-result.json", installer, StringComparison.Ordinal);
        Assert.Contains("Initialize-FlashNextWinGetSource", dependencies, StringComparison.Ordinal);
        Assert.Contains("Get-FlashNextDependencyProbe", dependencies, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-WinGetInstalledVersion", dependencies, StringComparison.Ordinal);

        Assert.Contains("dotnet-restore", build, StringComparison.Ordinal);
        Assert.Contains("dotnet-tests", build, StringComparison.Ordinal);
        Assert.Contains("manager-publish", build, StringComparison.Ordinal);
        Assert.Contains("Build-Runtime.ps1", build, StringComparison.Ordinal);
        Assert.Contains("inventories", build, StringComparison.Ordinal);

        Assert.Contains("build-result-validation", machine, StringComparison.Ordinal);
        Assert.Contains("staging-integrity-validation", machine, StringComparison.Ordinal);
        Assert.Contains("atomic-activation", machine, StringComparison.Ordinal);
        Assert.Contains("Write-FlashNextOperationResult", machine, StringComparison.Ordinal);
        Assert.DoesNotContain("winget", machine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet", machine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pip", machine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Build-Runtime.ps1", machine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmake", machine, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Structured dependency-stage result", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Structured build-stage result", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Structured machine-stage result", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelDownloaderUsesTheCurrentUsersPythonEnvironment()
    {
        string modelManager = File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Infrastructure.Windows", "Models", "ModelManager.cs"));
        Assert.Contains("Environment.SpecialFolder.LocalApplicationData", modelManager, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.SpecialFolder.ProgramFiles), \"FlashNextManager\", \"python-env\"", modelManager, StringComparison.Ordinal);
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
