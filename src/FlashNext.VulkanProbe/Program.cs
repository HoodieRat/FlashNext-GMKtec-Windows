using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FlashNext.VulkanProbe;

internal static class Program
{
    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 1;
    internal const int ExitLoaderMissing = 2;
    internal const int ExitNoDevices = 3;
    internal const int ExitCancelled = 4;
    internal const int ExitInvalidArguments = 5;
    internal const int ExitTimeout = 124;

    private const int VkSuccess = 0;
    private const int VkStructureTypeApplicationInfo = 0;
    private const int VkStructureTypeInstanceCreateInfo = 1;
    private const uint VkApiVersion10 = 1u << 22;
    private const uint VkMemoryHeapDeviceLocalBit = 0x00000001;
    private const int PhysicalDevicePropertiesBytes = 2048;
    private const int PhysicalDeviceMemoryPropertiesBytes = 1024;
    private const int DeviceNameOffset = 20;
    private const int DeviceNameBytes = 256;
    private const int MemoryHeapCountOffset = 260;
    private const int MemoryHeapsOffset = 264;
    private const int MemoryHeapStride = 16;
    private const int MaxMemoryHeaps = 16;
    private const int MaxPhysicalDevices = 32;

    private static int s_cancelRequested;
    private static int s_exitCode = ExitFailure;

    private static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        Console.CancelKeyPress += static (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref s_cancelRequested, 1);
            WriteDiagnostic("Cancellation requested.");
        };

        int timeoutMilliseconds = 15000;
        try
        {
            timeoutMilliseconds = ParseTimeout(args);
        }
        catch (ArgumentException exception)
        {
            WriteDiagnostic(exception.Message);
            WriteFailureJson(loaderPath: string.Empty, error: exception.Message, devicesJson: "[]", targetFound: false, largestTarget: 0);
            return ExitInvalidArguments;
        }

        using CancellationTokenSource timeout = new();
        timeout.CancelAfter(timeoutMilliseconds);
        using Timer watchdog = new(_ =>
        {
            if (timeout.IsCancellationRequested)
            {
                Interlocked.Exchange(ref s_cancelRequested, 1);
            }
        }, null, 250, 250);

        try
        {
            ProbeResult result = RunProbe(timeout.Token);
            WriteJson(result);
            Console.Out.Flush();
            if (!string.IsNullOrEmpty(result.Error)) WriteDiagnostic(result.Error);
            else if (result.Success) WriteDiagnostic("Enumerated Vulkan physical devices and DEVICE_LOCAL heaps.");
            s_exitCode = result.ExitCode;
            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            bool timedOut = timeout.IsCancellationRequested && s_cancelRequested == 0;
            string error = timedOut
                ? $"The Vulkan memory probe exceeded the {timeoutMilliseconds} ms limit."
                : "The Vulkan memory probe was cancelled.";
            WriteDiagnostic(error);
            WriteFailureJson(loaderPath: string.Empty, error: error, devicesJson: "[]", targetFound: false, largestTarget: 0);
            s_exitCode = timedOut ? ExitTimeout : ExitCancelled;
            return s_exitCode;
        }
        catch (Exception exception)
        {
            WriteDiagnostic(exception.Message);
            WriteFailureJson(loaderPath: string.Empty, error: exception.Message, devicesJson: "[]", targetFound: false, largestTarget: 0);
            s_exitCode = ExitFailure;
            return ExitFailure;
        }
    }

    private static int ParseTimeout(string[] args)
    {
        int timeoutMilliseconds = 15000;
        for (int index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(args[index], "-h", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("FlashNext Vulkan memory probe. Enumerates physical devices and device-local heaps only.");
                Console.Error.WriteLine("Usage: FlashNext.VulkanProbe.exe [--timeout-ms 15000]");
                Environment.Exit(ExitSuccess);
            }
            if (string.Equals(args[index], "--timeout-ms", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutMilliseconds) || timeoutMilliseconds < 250 || timeoutMilliseconds > 120000)
                {
                    throw new ArgumentException("Usage: FlashNext.VulkanProbe.exe [--timeout-ms 15000]");
                }
                index++;
                continue;
            }
            if (args[index].StartsWith("--timeout-ms=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = args[index]["--timeout-ms=".Length..];
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutMilliseconds) || timeoutMilliseconds < 250 || timeoutMilliseconds > 120000)
                {
                    throw new ArgumentException("Usage: FlashNext.VulkanProbe.exe [--timeout-ms 15000]");
                }
                continue;
            }
            throw new ArgumentException("Usage: FlashNext.VulkanProbe.exe [--timeout-ms 15000]");
        }
        return timeoutMilliseconds;
    }

    private static ProbeResult RunProbe(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCancelled();

        if (!NativeLibrary.TryLoad(ResolveLoaderPath(out string loaderPath), out nint library))
        {
            WriteDiagnostic("The Windows Vulkan loader (vulkan-1.dll) was not found. Install or repair the OEM/AMD graphics driver, then rerun install.cmd.");
            return new ProbeResult(ExitLoaderMissing, loaderPath, false, "The Windows Vulkan loader (vulkan-1.dll) was not found.", "[]", false, 0);
        }

        try
        {
            vkCreateInstance createInstance = Get<vkCreateInstance>(library, "vkCreateInstance");
            vkEnumeratePhysicalDevices enumerateDevices = Get<vkEnumeratePhysicalDevices>(library, "vkEnumeratePhysicalDevices");
            vkGetPhysicalDeviceProperties getProperties = Get<vkGetPhysicalDeviceProperties>(library, "vkGetPhysicalDeviceProperties");
            vkGetPhysicalDeviceMemoryProperties getMemory = Get<vkGetPhysicalDeviceMemoryProperties>(library, "vkGetPhysicalDeviceMemoryProperties");
            vkDestroyInstance destroyInstance = Get<vkDestroyInstance>(library, "vkDestroyInstance");

            nint applicationName = Marshal.StringToHGlobalAnsi("FlashNext.VulkanProbe");
            nint engineName = Marshal.StringToHGlobalAnsi("FlashNext");
            nint applicationInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            nint createInfo = Marshal.AllocHGlobal(Marshal.SizeOf<VkInstanceCreateInfo>());
            nint instance = IntPtr.Zero;
            try
            {
                VkApplicationInfo app = new()
                {
                    sType = VkStructureTypeApplicationInfo,
                    pNext = IntPtr.Zero,
                    pApplicationName = applicationName,
                    applicationVersion = VkApiVersion10,
                    pEngineName = engineName,
                    engineVersion = VkApiVersion10,
                    apiVersion = VkApiVersion10
                };
                Marshal.StructureToPtr(app, applicationInfo, false);
                VkInstanceCreateInfo info = new()
                {
                    sType = VkStructureTypeInstanceCreateInfo,
                    pNext = IntPtr.Zero,
                    flags = 0,
                    pApplicationInfo = applicationInfo,
                    enabledLayerCount = 0,
                    ppEnabledLayerNames = IntPtr.Zero,
                    enabledExtensionCount = 0,
                    ppEnabledExtensionNames = IntPtr.Zero
                };
                Marshal.StructureToPtr(info, createInfo, false);

                ThrowIfCancelled();
                int created = createInstance(createInfo, IntPtr.Zero, out instance);
                if (created != VkSuccess || instance == IntPtr.Zero)
                {
                    string error = "vkCreateInstance failed with code " + created.ToString(CultureInfo.InvariantCulture) + ".";
                    WriteDiagnostic(error);
                    return new ProbeResult(ExitFailure, loaderPath, false, error, "[]", false, 0);
                }

                uint count = 0;
                int enumerated = enumerateDevices(instance, ref count, IntPtr.Zero);
                if (enumerated != VkSuccess)
                {
                    string error = "vkEnumeratePhysicalDevices failed with code " + enumerated.ToString(CultureInfo.InvariantCulture) + ".";
                    WriteDiagnostic(error);
                    return new ProbeResult(ExitFailure, loaderPath, false, error, "[]", false, 0);
                }
                if (count == 0)
                {
                    const string error = "Vulkan reported zero physical devices.";
                    WriteDiagnostic(error);
                    return new ProbeResult(ExitNoDevices, loaderPath, false, error, "[]", false, 0);
                }
                if (count > MaxPhysicalDevices) count = MaxPhysicalDevices;

                nint deviceBuffer = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
                nint propertiesBuffer = Marshal.AllocHGlobal(PhysicalDevicePropertiesBytes);
                nint memoryBuffer = Marshal.AllocHGlobal(PhysicalDeviceMemoryPropertiesBytes);
                try
                {
                    ThrowIfCancelled();
                    int filled = enumerateDevices(instance, ref count, deviceBuffer);
                    if (filled != VkSuccess)
                    {
                        string error = "vkEnumeratePhysicalDevices (fill) failed with code " + filled.ToString(CultureInfo.InvariantCulture) + ".";
                        WriteDiagnostic(error);
                        return new ProbeResult(ExitFailure, loaderPath, false, error, "[]", false, 0);
                    }

                    StringBuilder devicesJson = new();
                    bool targetFound = false;
                    ulong largestTarget = 0;
                    for (int index = 0; index < count; index++)
                    {
                        ThrowIfCancelled();
                        nint device = Marshal.ReadIntPtr(deviceBuffer, index * IntPtr.Size);
                        Zero(propertiesBuffer, PhysicalDevicePropertiesBytes);
                        Zero(memoryBuffer, PhysicalDeviceMemoryPropertiesBytes);
                        getProperties(device, propertiesBuffer);
                        getMemory(device, memoryBuffer);

                        uint apiVersion = ReadUInt32(propertiesBuffer, 0);
                        uint driverVersion = ReadUInt32(propertiesBuffer, 4);
                        uint vendorId = ReadUInt32(propertiesBuffer, 8);
                        uint deviceId = ReadUInt32(propertiesBuffer, 12);
                        string name = ReadFixedUtf8(propertiesBuffer, DeviceNameOffset, DeviceNameBytes);
                        bool isTarget = IsTargetRadeon8060S(name);
                        List<HeapInfo> heaps = ReadHeaps(memoryBuffer);
                        ulong largestLocal = 0;
                        ulong totalLocal = 0;
                        foreach (HeapInfo heap in heaps)
                        {
                            if (!heap.DeviceLocal) continue;
                            totalLocal += heap.SizeBytes;
                            if (heap.SizeBytes > largestLocal) largestLocal = heap.SizeBytes;
                        }
                        if (isTarget)
                        {
                            targetFound = true;
                            if (largestLocal > largestTarget) largestTarget = largestLocal;
                        }

                        if (devicesJson.Length > 0) devicesJson.Append(',');
                        devicesJson.Append('{');
                        devicesJson.Append("\"name\":\"").Append(Escape(name)).Append('"');
                        devicesJson.Append(",\"apiVersion\":\"").Append(Escape(FormatVersion(apiVersion))).Append('"');
                        devicesJson.Append(",\"driverVersion\":\"").Append(Escape(FormatVersion(driverVersion))).Append('"');
                        devicesJson.Append(",\"driverVersionRaw\":").Append(driverVersion.ToString(CultureInfo.InvariantCulture));
                        devicesJson.Append(",\"vendorId\":").Append(vendorId.ToString(CultureInfo.InvariantCulture));
                        devicesJson.Append(",\"deviceId\":").Append(deviceId.ToString(CultureInfo.InvariantCulture));
                        devicesJson.Append(",\"isTargetRadeon8060S\":").Append(isTarget ? "true" : "false");
                        devicesJson.Append(",\"memoryHeaps\":[");
                        for (int heapIndex = 0; heapIndex < heaps.Count; heapIndex++)
                        {
                            if (heapIndex > 0) devicesJson.Append(',');
                            HeapInfo heap = heaps[heapIndex];
                            devicesJson.Append("{\"index\":").Append(heap.Index.ToString(CultureInfo.InvariantCulture));
                            devicesJson.Append(",\"sizeBytes\":").Append(heap.SizeBytes.ToString(CultureInfo.InvariantCulture));
                            devicesJson.Append(",\"deviceLocal\":").Append(heap.DeviceLocal ? "true" : "false");
                            devicesJson.Append('}');
                        }
                        devicesJson.Append(']');
                        devicesJson.Append(",\"largestDeviceLocalHeapBytes\":").Append(largestLocal.ToString(CultureInfo.InvariantCulture));
                        devicesJson.Append(",\"totalDeviceLocalHeapBytes\":").Append(totalLocal.ToString(CultureInfo.InvariantCulture));
                        devicesJson.Append('}');
                    }

                    return new ProbeResult(ExitSuccess, loaderPath, true, string.Empty, devicesJson.ToString(), targetFound, largestTarget);
                }
                finally
                {
                    Marshal.FreeHGlobal(memoryBuffer);
                    Marshal.FreeHGlobal(propertiesBuffer);
                    Marshal.FreeHGlobal(deviceBuffer);
                }
            }
            finally
            {
                if (instance != IntPtr.Zero)
                {
                    destroyInstance(instance, IntPtr.Zero);
                }
                Marshal.FreeHGlobal(createInfo);
                Marshal.FreeHGlobal(applicationInfo);
                Marshal.FreeHGlobal(engineName);
                Marshal.FreeHGlobal(applicationName);
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static string ResolveLoaderPath(out string reported)
    {
        List<string> candidates = [];
        string system = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
        candidates.Add(system);
        string? sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            candidates.Add(Path.Combine(sdk, "Bin", "vulkan-1.dll"));
            candidates.Add(Path.Combine(sdk, "Runtime", "x64", "vulkan-1.dll"));
        }
        const string sdkRoot = @"C:\VulkanSDK";
        if (Directory.Exists(sdkRoot))
        {
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(sdkRoot).OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(directory, "Bin", "vulkan-1.dll"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                reported = candidate;
                return candidate;
            }
        }

        reported = "vulkan-1.dll";
        return "vulkan-1.dll";
    }

    private static T Get<T>(nint library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out nint address) || address == IntPtr.Zero)
        {
            throw new InvalidOperationException("The Vulkan loader is missing export '" + name + "'.");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static List<HeapInfo> ReadHeaps(nint memory)
    {
        List<HeapInfo> heaps = [];
        uint heapCount = ReadUInt32(memory, MemoryHeapCountOffset);
        if (heapCount > MaxMemoryHeaps) heapCount = MaxMemoryHeaps;
        for (int index = 0; index < heapCount; index++)
        {
            int offset = MemoryHeapsOffset + (index * MemoryHeapStride);
            ulong size = ReadUInt64(memory, offset);
            uint flags = ReadUInt32(memory, offset + 8);
            heaps.Add(new HeapInfo(index, size, (flags & VkMemoryHeapDeviceLocalBit) != 0));
        }
        return heaps;
    }

    private static bool IsTargetRadeon8060S(string name)
    {
        string normalized = Normalize(name);
        bool radeon = false;
        bool token8060S = false;
        foreach (string token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "RADEON") radeon = true;
            if (token == "8060S") token8060S = true;
        }
        return radeon && token8060S;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        StringBuilder builder = new(value.Length);
        bool previousWasSeparator = true;
        foreach (char character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }
        return builder.ToString().Trim();
    }

    private static string FormatVersion(uint value)
    {
        uint major = (value >> 22) & 0x7Fu;
        uint minor = (value >> 12) & 0x3FFu;
        uint patch = value & 0xFFFu;
        return major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture) + "." + patch.ToString(CultureInfo.InvariantCulture);
    }

    private static string ReadFixedUtf8(nint pointer, int offset, int maximum)
    {
        byte[] buffer = new byte[maximum];
        Marshal.Copy(pointer + offset, buffer, 0, maximum);
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = maximum;
        return Encoding.UTF8.GetString(buffer, 0, length).Trim();
    }

    private static uint ReadUInt32(nint pointer, int offset) => unchecked((uint)Marshal.ReadInt32(pointer, offset));

    private static ulong ReadUInt64(nint pointer, int offset) => unchecked((ulong)Marshal.ReadInt64(pointer, offset));

    private static void Zero(nint pointer, int length)
    {
        for (int index = 0; index < length; index++) Marshal.WriteByte(pointer, index, 0);
    }

    private static void ThrowIfCancelled()
    {
        if (Interlocked.CompareExchange(ref s_cancelRequested, 0, 0) != 0) throw new OperationCanceledException();
    }

    private static void WriteJson(ProbeResult result)
    {
        StringBuilder json = new();
        json.Append('{');
        json.Append("\"schemaVersion\":1");
        json.Append(",\"success\":").Append(result.Success ? "true" : "false");
        json.Append(",\"loaderPath\":\"").Append(Escape(result.LoaderPath)).Append('"');
        json.Append(",\"error\":");
        if (string.IsNullOrEmpty(result.Error)) json.Append("null");
        else json.Append('"').Append(Escape(result.Error)).Append('"');
        json.Append(",\"devices\":[").Append(result.DevicesJson).Append(']');
        json.Append(",\"targetDeviceFound\":").Append(result.TargetFound ? "true" : "false");
        json.Append(",\"largestTargetDeviceLocalHeapBytes\":").Append(result.LargestTarget.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"exitCode\":").Append(result.ExitCode.ToString(CultureInfo.InvariantCulture));
        json.Append('}');
        Console.Out.Write(json.ToString());
        Console.Out.Flush();
    }

    private static void WriteFailureJson(string loaderPath, string error, string devicesJson, bool targetFound, ulong largestTarget)
    {
        WriteJson(new ProbeResult(s_exitCode == ExitSuccess ? ExitFailure : s_exitCode, loaderPath, false, error, devicesJson, targetFound, largestTarget));
    }

    private static void WriteDiagnostic(string message)
    {
        Console.Error.WriteLine("flashnext-vulkan-probe: " + message);
        Console.Error.Flush();
    }

    private static string Escape(string value)
    {
        StringBuilder builder = new(value.Length + 8);
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 32)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }

    private readonly record struct ProbeResult(int ExitCode, string LoaderPath, bool Success, string Error, string DevicesJson, bool TargetFound, ulong LargestTarget);
    private readonly record struct HeapInfo(int Index, ulong SizeBytes, bool DeviceLocal);

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public int sType;
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public uint applicationVersion;
        public IntPtr pEngineName;
        public uint engineVersion;
        public uint apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int vkCreateInstance(IntPtr createInfo, IntPtr allocator, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void vkGetPhysicalDeviceMemoryProperties(IntPtr device, IntPtr memory);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void vkDestroyInstance(IntPtr instance, IntPtr allocator);
}
