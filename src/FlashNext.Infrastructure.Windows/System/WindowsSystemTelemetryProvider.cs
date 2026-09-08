using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using FlashNext.Core.Interfaces;
using FlashNext.Core.Models;

namespace FlashNext.Infrastructure.Windows.System;

public sealed class WindowsSystemTelemetryProvider : ISystemTelemetryProvider
{
    public Task<SystemTelemetry> CaptureAsync(int? processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SystemTelemetry result = new();
        if (processId is int id)
        {
            try
            {
                using Process process = Process.GetProcessById(id);
                process.Refresh();
                result.WorkingSetBytes = process.WorkingSet64;
                result.PeakWorkingSetBytes = process.PeakWorkingSet64;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        if (OperatingSystem.IsWindows())
        {
            CaptureAvailableMemory(result);
            CaptureCpu(result);
            if (processId is int gpuProcessId) CaptureGpuCounters(gpuProcessId, result);
        }
        return Task.FromResult(result);
    }

    private static void CaptureAvailableMemory(SystemTelemetry result)
    {
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT FreePhysicalMemory, TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject item in searcher.Get())
            {
                ulong freeKib = Convert.ToUInt64(item["FreePhysicalMemory"], CultureInfo.InvariantCulture);
                result.AvailableSystemMemoryBytes = checked((long)(freeKib * 1024UL));
                ulong totalKib = Convert.ToUInt64(item["TotalVisibleMemorySize"], CultureInfo.InvariantCulture);
                result.PhysicalMemoryBytes = checked((long)(totalKib * 1024UL));
                break;
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
        }
    }

    private static void CaptureCpu(SystemTelemetry result)
    {
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT LoadPercentage FROM Win32_Processor");
            double total = 0;
            int count = 0;
            foreach (ManagementObject item in searcher.Get())
            {
                object? load = item["LoadPercentage"];
                if (load is null) continue;
                total += double.Parse(load.ToString() ?? "0", CultureInfo.InvariantCulture);
                count++;
            }
            if (count > 0) result.CpuUtilizationPercent = Math.Clamp(total / count, 0, 100);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException or FormatException)
        {
        }
    }

    private static void CaptureGpuCounters(int processId, SystemTelemetry result)
    {
        string pidMarker = "pid_" + processId.ToString(CultureInfo.InvariantCulture) + "_";
        try
        {
            double maximum = 0;
            bool found = false;
            using ManagementObjectSearcher engineSearcher = new("SELECT Name,UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            foreach (ManagementObject item in engineSearcher.Get())
            {
                string name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!name.Contains(pidMarker, StringComparison.OrdinalIgnoreCase)) continue;
                double utilization = double.Parse(Convert.ToString(item["UtilizationPercentage"], CultureInfo.InvariantCulture) ?? "0", CultureInfo.InvariantCulture);
                maximum = Math.Max(maximum, utilization);
                found = true;
            }
            if (found) result.GpuUtilizationPercent = Math.Clamp(maximum, 0, 100);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException or FormatException)
        {
        }

        try
        {
            ulong total = 0;
            bool found = false;
            using ManagementObjectSearcher memorySearcher = new("SELECT Name,DedicatedUsage,SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory");
            foreach (ManagementObject item in memorySearcher.Get())
            {
                string name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!name.Contains(pidMarker, StringComparison.OrdinalIgnoreCase)) continue;
                total += Convert.ToUInt64(item["DedicatedUsage"], CultureInfo.InvariantCulture);
                total += Convert.ToUInt64(item["SharedUsage"], CultureInfo.InvariantCulture);
                found = true;
            }
            if (found && total <= long.MaxValue) result.GpuMemoryUsedBytes = (long)total;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException or InvalidOperationException or FormatException or OverflowException)
        {
        }
    }
}
