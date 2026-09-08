namespace FlashNext.InstallationTests;

public sealed class DependencyVerificationContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void DependencySuccessUsesDirectCapabilityProbesInsteadOfWingetTableParsing()
    {
        string installer = File.ReadAllText(Path.Combine(Root, "scripts", "Install-Dependencies.ps1"));
        string probes = File.ReadAllText(Path.Combine(Root, "scripts", "Dependency-Probes.ps1"));
        string manifest = File.ReadAllText(Path.Combine(Root, "manifests", "dependencies.lock.json"));
        string bootstrap = File.ReadAllText(Path.Combine(Root, "scripts", "Test-BootstrapHardware.ps1"));

        Assert.DoesNotContain("Get-WinGetInstalledVersion", installer, StringComparison.Ordinal);
        Assert.Contains("Get-FlashNextDependencyProbe", installer, StringComparison.Ordinal);
        Assert.Contains("Wait-FlashNextDependencyProbe", installer, StringComparison.Ordinal);
        Assert.Contains("Get-FlashNextWinGetDiagnosticTail", installer, StringComparison.Ordinal);
        Assert.Contains("direct capability probe passed; continuing with the verified tool", installer, StringComparison.Ordinal);

        Assert.Contains("git version 2.55.0.windows.3", probes, StringComparison.Ordinal);
        Assert.Contains("--list-sdks", probes, StringComparison.Ordinal);
        Assert.Contains("struct.calcsize", probes, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", probes, StringComparison.Ordinal);
        Assert.Contains("VC\\Tools\\MSVC", probes, StringComparison.Ordinal);
        Assert.Contains("Include\\vulkan\\vulkan.h", probes, StringComparison.Ordinal);
        Assert.Contains("Lib\\vulkan-1.lib", probes, StringComparison.Ordinal);
        Assert.Contains("vulkaninfoSDK.exe", probes, StringComparison.Ordinal);
        Assert.Contains("vulkaninfo.exe", probes, StringComparison.Ordinal);
        Assert.Contains("Compile capability is", probes, StringComparison.Ordinal);
        Assert.Contains("DEVICE_LOCAL", bootstrap, StringComparison.Ordinal);
        Assert.Contains("96 GB", bootstrap, StringComparison.Ordinal);

        Assert.Contains("human-formatted winget list output is diagnostic only", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReceivesTheVerifiedVulkanSdkPath()
    {
        string installer = File.ReadAllText(Path.Combine(Root, "scripts", "Install-FlashNext.ps1"));
        string build = File.ReadAllText(Path.Combine(Root, "scripts", "Build-FlashNext.ps1"));
        string runtime = File.ReadAllText(Path.Combine(Root, "scripts", "Build-Runtime.ps1"));

        Assert.Contains("VulkanSdkRoot = $vulkanSdkRoot", installer, StringComparison.Ordinal);
        Assert.Contains("[Parameter(Mandatory=$true)][string]$VulkanSdkRoot", build, StringComparison.Ordinal);
        Assert.Contains("-VulkanSdkRoot $vulkanSdk", build, StringComparison.Ordinal);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", build, StringComparison.Ordinal);
        Assert.DoesNotContain("-requires','Microsoft.VisualStudio.Workload.VCTools", build, StringComparison.Ordinal);
        Assert.Contains("Bin\\glslc.exe", runtime, StringComparison.Ordinal);
        Assert.Contains("Include\\vulkan\\vulkan.h", runtime, StringComparison.Ordinal);
        Assert.Contains("Lib\\vulkan-1.lib", runtime, StringComparison.Ordinal);
        Assert.Contains("Visual Studio 18 2026", runtime, StringComparison.Ordinal);
        Assert.Contains("Visual Studio 17 2022", runtime, StringComparison.Ordinal);
        Assert.Contains("CMAKE_GENERATOR_INSTANCE", runtime, StringComparison.Ordinal);
        Assert.Contains("CMAKE_GENERATOR_INSTANCE:INTERNAL", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("-G','Visual Studio 17 2022", runtime, StringComparison.Ordinal);
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
