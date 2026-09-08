using System.Diagnostics;
using FlashNext.Core.Services;

namespace FlashNext.InstallationTests;

public sealed class LiveVulkanProbeTests
{
    [Fact]
    public async Task BootstrapProbeCompletesQuicklyAndReportsJson()
    {
        string root = FindRoot();
        string probe = Path.Combine(root, "bootstrap", "FlashNext.VulkanProbe.exe");
        Assert.True(File.Exists(probe), probe);

        ProcessStartInfo start = new()
        {
            FileName = probe,
            WorkingDirectory = Path.GetDirectoryName(probe),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--timeout-ms");
        start.ArgumentList.Add("15000");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the bootstrap Vulkan probe.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource wait = new(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(wait.Token);
        string output = await stdout;
        string error = await stderr;
        Assert.Equal(0, process.ExitCode);
        var evaluation = VulkanProbeEvaluator.Evaluate(output);
        Assert.True(evaluation.Parsed);
        Assert.True(evaluation.Document is { Success: true } || evaluation.DevicesPresent, output + Environment.NewLine + error);
        Assert.True(evaluation.TargetDeviceFound, "Expected the Radeon 8060S on this factory machine.");
        Assert.True(evaluation.MeetsFactoryUmaRequirement, evaluation.FailureMessage);
        Assert.True(evaluation.LargestTargetDeviceLocalHeapBytes >= VulkanProbeEvaluator.FactoryUmaHeapBytes);
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
