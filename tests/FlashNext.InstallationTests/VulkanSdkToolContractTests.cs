namespace FlashNext.InstallationTests;

public sealed class VulkanSdkToolContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void WindowsSdkToolUsesOfficialVulkanInfoSdkNameWithRuntimeFallback()
    {
        string probes = File.ReadAllText(Path.Combine(Root, "scripts", "Dependency-Probes.ps1"));
        string bootstrap = File.ReadAllText(Path.Combine(Root, "scripts", "Test-BootstrapHardware.ps1"));
        string managerProbe = File.ReadAllText(Path.Combine(Root, "src", "FlashNext.Infrastructure.Windows", "Hardware", "WindowsHardwareProbe.cs"));

        Assert.Contains("vulkaninfoSDK.exe", probes, StringComparison.Ordinal);
        Assert.Contains("vulkaninfo.exe", probes, StringComparison.Ordinal);
        Assert.Contains("FlashNext.VulkanProbe.exe", bootstrap, StringComparison.Ordinal);
        Assert.Contains("direct-vulkan-memory-probe", bootstrap, StringComparison.Ordinal);
        Assert.Contains("VulkanProbeClient", managerProbe, StringComparison.Ordinal);
        Assert.Contains("glslc", probes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Include\\vulkan\\vulkan.h", probes, StringComparison.Ordinal);
        Assert.Contains("Lib\\vulkan-1.lib", probes, StringComparison.Ordinal);
        Assert.DoesNotContain("The Vulkan SDK is missing vulkaninfo.exe", bootstrap, StringComparison.Ordinal);
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
