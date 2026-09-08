using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;
using FlashNext.Core.Services;
using FlashNext.Infrastructure.Windows.System;

namespace FlashNext.Infrastructure.Windows.Hardware;

public sealed class WindowsHardwareProbe(IProcessRunner processRunner, PlatformPaths paths) : IHardwareProbe
{
    private readonly VulkanProbeClient _probeClient = new(processRunner, paths);
    private const ulong GiB = 1024UL * 1024UL * 1024UL;

    public async Task<HardwareReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        HardwareReport report = new()
        {
            WindowsVersion = RuntimeInformation.OSDescription,
            IsWindows11X64 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && Environment.Is64BitOperatingSystem,
            VulkanProbeKind = "direct-vulkan-memory-probe"
        };

        if (!OperatingSystem.IsWindows())
        {
            report.Errors.Add("This project runs only on Windows 11 x64.");
            return report;
        }

        report.CpuName = QuerySingle("SELECT Name FROM Win32_Processor", "Name");
        report.OsVisibleMemoryBytes = QueryUInt64First("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory");
        (report.PhysicalMemoryBytes, report.PhysicalMemoryDetectionSource) = QueryInstalledPhysicalMemory();
        (report.GpuName, report.GpuDriverVersion) = QueryGpu();

        report.MeetsCpuRequirement = HardwareIdentity.IsTargetCpu(report.CpuName);
        report.MeetsPhysicalMemoryRequirement = report.PhysicalMemoryBytes >= 120UL * GiB;

        if (!report.MeetsCpuRequirement)
        {
            report.Errors.Add($"Expected AMD Ryzen AI Max+ 395; detected '{report.CpuName}'.");
        }

        if (report.PhysicalMemoryBytes == 0)
        {
            report.Errors.Add("Windows could not determine the physically installed memory capacity from SMBIOS. Update the EVO-X2 BIOS/firmware if needed and rerun preflight.");
        }
        else if (!report.MeetsPhysicalMemoryRequirement)
        {
            report.Errors.Add($"At least 120 GiB physically installed memory is required; detected {report.PhysicalMemoryBytes / (double)GiB:F1} GiB from {report.PhysicalMemoryDetectionSource}.");
        }

        if (report.OsVisibleMemoryBytes > 0 && report.PhysicalMemoryBytes > report.OsVisibleMemoryBytes + 8UL * GiB)
        {
            report.Warnings.Add($"Windows currently exposes {report.OsVisibleMemoryBytes / (double)GiB:F1} GiB to applications out of {report.PhysicalMemoryBytes / (double)GiB:F1} GiB physically installed. A large firmware UMA reservation can cause this; the Vulkan heap check determines whether the required 96 GB GPU-visible allocation is present.");
        }

        await ApplyDirectVulkanProbeAsync(report, cancellationToken).ConfigureAwait(false);

        report.MeetsGpuRequirement = HardwareIdentity.IsTargetGpu(report.GpuName) || HardwareIdentity.IsTargetGpu(report.VulkanDeviceName) || report.VulkanTargetDeviceFound;
        if (!report.MeetsGpuRequirement)
        {
            string detected = string.IsNullOrWhiteSpace(report.VulkanDeviceName)
                ? report.GpuName
                : $"{report.GpuName}; Vulkan: {report.VulkanDeviceName}";
            report.Errors.Add($"Expected the Radeon 8060S; detected '{detected}'.");
        }

        string runtimeRoot = Path.GetFullPath(Path.Combine(paths.ApplicationDirectory, "..", "..", "runtime", "current"));
        string llamaCli = Path.Combine(runtimeRoot, "llama-cli.exe");
        if (File.Exists(llamaCli))
        {
            ProcessResult devices = await processRunner.RunAsync(new ProcessSpec(llamaCli, ["--list-devices"]), TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            string deviceText = devices.StandardOutput + Environment.NewLine + devices.StandardError;
            if (!deviceText.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) && !HardwareIdentity.IsTargetGpu(deviceText))
            {
                report.Warnings.Add("The current runtime did not clearly report the Radeon Vulkan device. Rebuild the runtime and inspect diagnostics.");
            }
        }

        report.MeetsGpuHeapRequirement = report.LargestDeviceLocalHeapBytes is ulong heap && heap >= VulkanProbeEvaluator.FactoryUmaHeapBytes;
        if (!report.MeetsGpuHeapRequirement)
        {
            if (report.LargestDeviceLocalHeapBytes is ulong detectedHeap)
            {
                report.Errors.Add($"Large GPU-visible memory is insufficient ({detectedHeap / (double)GiB:F1} GiB detected). {VulkanProbeEvaluator.BiosUmaInstruction} The installer will not change BIOS settings.");
            }
            else if (!report.Errors.Exists(static item => item.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)))
            {
                report.Errors.Add($"The Vulkan device-local memory heap could not be measured. Confirm the OEM/AMD graphics driver is working, then rerun install.cmd. If Vulkan reports a heap below 90 GiB, {VulkanProbeEvaluator.BiosUmaInstruction}");
            }
        }

        if (string.IsNullOrWhiteSpace(report.GpuDriverVersion))
        {
            report.Warnings.Add("The AMD graphics driver version could not be read. Use the GMKtec/OEM-supported driver unless a newer AMD driver has been validated for this system.");
        }

        return report;
    }

    private async Task ApplyDirectVulkanProbeAsync(HardwareReport report, CancellationToken cancellationToken)
    {
        string? executable = _probeClient.FindExecutable();
        if (executable is null)
        {
            report.Errors.Add("FlashNext.VulkanProbe.exe is missing. Restore the source package or rebuild the manager, then rerun install.cmd.");
            return;
        }

        report.VulkanProbeExecutable = executable;
        VulkanProbeRun run;
        try
        {
            run = await _probeClient.RunAsync(VulkanProbeClient.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or InvalidOperationException)
        {
            report.Errors.Add(exception.Message);
            return;
        }

        report.VulkanRawSummary = TrimTo(run.DiagnosticTail, 24000);
        if (run.TimedOut)
        {
            report.Errors.Add($"The direct Vulkan memory probe timed out after {VulkanProbeClient.DefaultTimeoutSeconds} seconds (PID {run.Process.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}). The process tree was terminated. Rerun install.cmd.");
            return;
        }

        try
        {
            VulkanProbeEvaluation evaluation = VulkanProbeEvaluator.Evaluate(VulkanProbeEvaluator.ExtractJsonObject(run.StandardOutput));
            report.VulkanLoaderPath = evaluation.LoaderPath;
            report.VulkanTargetDeviceFound = evaluation.TargetDeviceFound;
            report.VulkanDeviceName = evaluation.TargetDeviceName ?? evaluation.Document?.Devices.FirstOrDefault()?.Name ?? string.Empty;
            report.VulkanApiVersion = evaluation.ApiVersion ?? string.Empty;
            report.VulkanDriverVersion = evaluation.DriverVersion ?? string.Empty;
            if (evaluation.TargetDevice is VulkanProbeDevice target)
            {
                report.LargestDeviceLocalHeapBytes = target.LargestDeviceLocalHeapBytes;
                report.TotalDeviceLocalHeapBytes = target.TotalDeviceLocalHeapBytes;
            }
            else if (evaluation.LargestTargetDeviceLocalHeapBytes > 0)
            {
                report.LargestDeviceLocalHeapBytes = evaluation.LargestTargetDeviceLocalHeapBytes;
                report.TotalDeviceLocalHeapBytes = evaluation.LargestTargetDeviceLocalHeapBytes;
            }

            if (!evaluation.MeetsFactoryUmaRequirement && !string.IsNullOrWhiteSpace(evaluation.FailureMessage))
            {
                report.Errors.Add(evaluation.FailureMessage);
            }
        }
        catch (InvalidDataException exception)
        {
            report.Errors.Add($"The Vulkan memory probe returned unusable output: {exception.Message}");
        }
    }

    private static (ulong Bytes, string Source) QueryInstalledPhysicalMemory()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes)
                && totalKilobytes > 0
                && totalKilobytes <= ulong.MaxValue / 1024UL)
            {
                return (totalKilobytes * 1024UL, "GetPhysicallyInstalledSystemMemory (SMBIOS)");
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
        }

        try
        {
            ulong moduleBytes = QueryUInt64Sum("SELECT Capacity FROM Win32_PhysicalMemory", "Capacity");
            if (moduleBytes > 0)
            {
                return (moduleBytes, "Win32_PhysicalMemory.Capacity sum");
            }
        }
        catch (ManagementException)
        {
        }

        return (0, "Unavailable");
    }

    private static (string Name, string Driver) QueryGpu()
    {
        List<(string Name, string Driver)> devices = [];
        using ManagementObjectSearcher searcher = new("SELECT Name,DriverVersion FROM Win32_VideoController");
        foreach (ManagementObject item in searcher.Get())
        {
            string name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? string.Empty;
            string driver = Convert.ToString(item["DriverVersion"], CultureInfo.InvariantCulture) ?? string.Empty;
            devices.Add((name, driver));
        }

        (string Name, string Driver) preferred = devices.FirstOrDefault(static device => HardwareIdentity.IsTargetGpu(device.Name));
        if (!string.IsNullOrWhiteSpace(preferred.Name)) return preferred;
        return (string.Join("; ", devices.Select(static device => device.Name)), string.Join("; ", devices.Select(static device => device.Driver)));
    }

    private static string QuerySingle(string query, string property)
    {
        using ManagementObjectSearcher searcher = new(query);
        foreach (ManagementObject item in searcher.Get())
        {
            return Convert.ToString(item[property], CultureInfo.InvariantCulture) ?? string.Empty;
        }
        return string.Empty;
    }

    private static ulong QueryUInt64First(string query, string property)
    {
        using ManagementObjectSearcher searcher = new(query);
        foreach (ManagementObject item in searcher.Get())
        {
            object? value = item[property];
            if (value is not null) return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        return 0;
    }

    private static ulong QueryUInt64Sum(string query, string property)
    {
        ulong total = 0;
        using ManagementObjectSearcher searcher = new(query);
        foreach (ManagementObject item in searcher.Get())
        {
            object? value = item[property];
            if (value is null) continue;
            ulong current = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            total = checked(total + current);
        }
        return total;
    }

    private static string TrimTo(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + Environment.NewLine + "[truncated]";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}
