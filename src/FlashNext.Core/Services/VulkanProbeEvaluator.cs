using System.Text.Json;
using FlashNext.Core.Models;

namespace FlashNext.Core.Services;

public static class VulkanProbeEvaluator
{
    public const ulong FactoryUmaHeapBytes = 90UL * 1024UL * 1024UL * 1024UL;
    public const string BiosUmaInstruction = "Set the GMKtec EVO-X2 BIOS UMA frame buffer to 96 GB, save the BIOS setting, reboot Windows, and rerun install.cmd.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static VulkanProbeDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("The Vulkan probe returned empty output.");
        try
        {
            VulkanProbeDocument? document = JsonSerializer.Deserialize<VulkanProbeDocument>(ExtractJsonObject(json), JsonOptions);
            if (document is null) throw new InvalidDataException("The Vulkan probe returned an empty JSON document.");
            if (document.SchemaVersion != 1) throw new InvalidDataException($"Unsupported Vulkan probe schema version '{document.SchemaVersion}'.");
            document.Devices ??= [];
            foreach (VulkanProbeDevice device in document.Devices)
            {
                device.MemoryHeaps ??= [];
                bool matched = HardwareIdentity.IsTargetGpu(device.Name);
                device.IsTargetRadeon8060S = matched;
                ulong largest = 0;
                ulong total = 0;
                foreach (VulkanProbeHeap heap in device.MemoryHeaps)
                {
                    if (!heap.DeviceLocal) continue;
                    total += heap.SizeBytes;
                    if (heap.SizeBytes > largest) largest = heap.SizeBytes;
                }
                if (device.LargestDeviceLocalHeapBytes == 0) device.LargestDeviceLocalHeapBytes = largest;
                if (device.TotalDeviceLocalHeapBytes == 0) device.TotalDeviceLocalHeapBytes = total;
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Vulkan probe returned malformed JSON.", exception);
        }
    }

    public static VulkanProbeEvaluation Evaluate(string json)
    {
        VulkanProbeDocument document = Parse(json);
        return Evaluate(document);
    }

    public static VulkanProbeEvaluation Evaluate(VulkanProbeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        VulkanProbeDevice? target = document.Devices.FirstOrDefault(static device => device.IsTargetRadeon8060S || HardwareIdentity.IsTargetGpu(device.Name));
        ulong largest = target?.LargestDeviceLocalHeapBytes ?? 0;
        if (largest == 0 && document.LargestTargetDeviceLocalHeapBytes > 0) largest = document.LargestTargetDeviceLocalHeapBytes;
        bool targetFound = target is not null || document.TargetDeviceFound;
        bool loaderFound = !string.IsNullOrWhiteSpace(document.LoaderPath) || document.Success;
        bool devicesPresent = document.Devices.Count > 0;
        bool uma = targetFound && largest >= FactoryUmaHeapBytes;

        string failure = string.Empty;
        if (!document.Success && !string.IsNullOrWhiteSpace(document.Error) && !devicesPresent)
        {
            failure = document.Error!;
        }
        else if (!loaderFound && !devicesPresent)
        {
            failure = "The Windows Vulkan loader (vulkan-1.dll) was not found. Install or repair the OEM/AMD graphics driver, then rerun install.cmd.";
        }
        else if (!devicesPresent)
        {
            failure = "Vulkan reported no physical devices. Install or repair the OEM/AMD graphics driver, then rerun install.cmd.";
        }
        else if (!targetFound)
        {
            string names = string.Join("; ", document.Devices.Select(static device => device.Name).Where(static name => !string.IsNullOrWhiteSpace(name)));
            failure = string.IsNullOrWhiteSpace(names)
                ? "Vulkan did not enumerate the Radeon 8060S."
                : $"Vulkan did not enumerate the Radeon 8060S. Devices: {names}";
        }
        else if (!uma)
        {
            failure = $"The Radeon 8060S Vulkan DEVICE_LOCAL heap is {largest / (1024d * 1024d * 1024d):F1} GiB; the factory configuration requires at least 90 GiB. {BiosUmaInstruction} The installer will not change BIOS settings.";
        }

        return new VulkanProbeEvaluation
        {
            Parsed = true,
            LoaderFound = loaderFound || devicesPresent,
            DevicesPresent = devicesPresent,
            TargetDeviceFound = targetFound,
            MeetsFactoryUmaRequirement = uma,
            LargestTargetDeviceLocalHeapBytes = largest,
            TargetDeviceName = target?.Name,
            ApiVersion = target?.ApiVersion,
            DriverVersion = target?.DriverVersion,
            LoaderPath = document.LoaderPath,
            FailureMessage = failure,
            Document = document,
            TargetDevice = target
        };
    }

    public static string ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The Vulkan probe returned empty output.");
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("The Vulkan probe output did not contain a JSON object.");
        return text[start..(end + 1)];
    }
}
