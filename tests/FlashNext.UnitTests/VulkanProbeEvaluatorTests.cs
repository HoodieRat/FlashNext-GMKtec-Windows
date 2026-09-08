using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class VulkanProbeEvaluatorTests
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;

    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics")]
    [InlineData("AMD Radeon 8060S")]
    [InlineData("AMD RADEON™ 8060S GRAPHICS")]
    [InlineData("Radeon-8060S")]
    public void TargetDeviceNameNormalizationMatches(string name)
    {
        Assert.True(HardwareIdentity.IsTargetGpu(name));
        string json = ProbeJson(name, 96 * GiB, extraDevice: false);
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.True(evaluation.TargetDeviceFound);
        Assert.Equal(name, evaluation.TargetDeviceName);
    }

    [Fact]
    public void HeapOf96GiBClassPasses()
    {
        string json = ProbeJson("AMD Radeon(TM) 8060S Graphics", 96 * GiB, extraDevice: true);
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.True(evaluation.MeetsFactoryUmaRequirement);
        Assert.True(evaluation.TargetDeviceFound);
        Assert.Equal(96 * GiB, evaluation.LargestTargetDeviceLocalHeapBytes);
        Assert.Equal(string.Empty, evaluation.FailureMessage);
    }

    [Fact]
    public void HeapOf64GiBClassFailsWithExactBiosInstruction()
    {
        string json = ProbeJson("AMD Radeon 8060S", 64 * GiB, extraDevice: false);
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.False(evaluation.MeetsFactoryUmaRequirement);
        Assert.Contains(VulkanProbeEvaluator.BiosUmaInstruction, evaluation.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("64.0 GiB", evaluation.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleDevicesSelectTheRadeonTarget()
    {
        string json = """
            {"schemaVersion":1,"success":true,"loaderPath":"C:\\Windows\\System32\\vulkan-1.dll","error":null,"devices":[
              {"name":"Microsoft Basic Render Driver","apiVersion":"1.3.0","driverVersion":"10.0.0","vendorId":5140,"deviceId":1,"isTargetRadeon8060S":false,"memoryHeaps":[{"index":0,"sizeBytes":1073741824,"deviceLocal":true}],"largestDeviceLocalHeapBytes":1073741824,"totalDeviceLocalHeapBytes":1073741824},
              {"name":"AMD Radeon(TM) 8060S Graphics","apiVersion":"1.4.357","driverVersion":"2.0.349","vendorId":4098,"deviceId":0,"isTargetRadeon8060S":true,"memoryHeaps":[{"index":0,"sizeBytes":103079215104,"deviceLocal":true},{"index":1,"sizeBytes":4294967296,"deviceLocal":false}],"largestDeviceLocalHeapBytes":103079215104,"totalDeviceLocalHeapBytes":103079215104}
            ],"targetDeviceFound":true,"largestTargetDeviceLocalHeapBytes":103079215104,"exitCode":0}
            """;
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.True(evaluation.TargetDeviceFound);
        Assert.Equal("AMD Radeon(TM) 8060S Graphics", evaluation.TargetDeviceName);
        Assert.True(evaluation.MeetsFactoryUmaRequirement);
        Assert.Equal(103079215104UL, evaluation.LargestTargetDeviceLocalHeapBytes);
    }

    [Fact]
    public void TargetDeviceAbsentFails()
    {
        string json = """
            {"schemaVersion":1,"success":true,"loaderPath":"vulkan-1.dll","error":null,"devices":[
              {"name":"NVIDIA GeForce RTX 5080","apiVersion":"1.3.0","driverVersion":"1.0.0","vendorId":4318,"deviceId":1,"isTargetRadeon8060S":false,"memoryHeaps":[{"index":0,"sizeBytes":25769803776,"deviceLocal":true}],"largestDeviceLocalHeapBytes":25769803776,"totalDeviceLocalHeapBytes":25769803776}
            ],"targetDeviceFound":false,"largestTargetDeviceLocalHeapBytes":0,"exitCode":0}
            """;
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.False(evaluation.TargetDeviceFound);
        Assert.False(evaluation.MeetsFactoryUmaRequirement);
        Assert.Contains("Radeon 8060S", evaluation.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderAbsentFails()
    {
        string json = """
            {"schemaVersion":1,"success":false,"loaderPath":"","error":"The Windows Vulkan loader (vulkan-1.dll) was not found.","devices":[],"targetDeviceFound":false,"largestTargetDeviceLocalHeapBytes":0,"exitCode":2}
            """;
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.False(evaluation.DevicesPresent);
        Assert.Contains("Vulkan loader", evaluation.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPhysicalDevicesFails()
    {
        string json = """
            {"schemaVersion":1,"success":false,"loaderPath":"C:\\Windows\\System32\\vulkan-1.dll","error":"Vulkan reported zero physical devices.","devices":[],"targetDeviceFound":false,"largestTargetDeviceLocalHeapBytes":0,"exitCode":3}
            """;
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.False(evaluation.DevicesPresent);
        Assert.Contains("zero physical devices", evaluation.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedProbeOutputThrows()
    {
        Assert.Throws<InvalidDataException>(() => VulkanProbeEvaluator.Parse("not json at all"));
        Assert.Throws<InvalidDataException>(() => VulkanProbeEvaluator.Parse(""));
        Assert.Throws<InvalidDataException>(() => VulkanProbeEvaluator.Parse("{\"schemaVersion\":2,\"success\":true,\"devices\":[]}"));
    }

    [Fact]
    public void ExtractsJsonObjectFromStderrNoise()
    {
        string wrapped = "flashnext-vulkan-probe: starting\n{\"schemaVersion\":1,\"success\":true,\"loaderPath\":\"vulkan-1.dll\",\"error\":null,\"devices\":[],\"targetDeviceFound\":false,\"largestTargetDeviceLocalHeapBytes\":0}\n";
        string extracted = VulkanProbeEvaluator.ExtractJsonObject(wrapped);
        Assert.StartsWith("{", extracted, StringComparison.Ordinal);
        Assert.EndsWith("}", extracted, StringComparison.Ordinal);
    }

    private static string ProbeJson(string name, ulong heapBytes, bool extraDevice)
    {
        string extra = extraDevice
            ? ",{\"name\":\"Microsoft Basic Render Driver\",\"apiVersion\":\"1.0.0\",\"driverVersion\":\"1.0.0\",\"vendorId\":1,\"deviceId\":1,\"isTargetRadeon8060S\":false,\"memoryHeaps\":[{\"index\":0,\"sizeBytes\":268435456,\"deviceLocal\":true}],\"largestDeviceLocalHeapBytes\":268435456,\"totalDeviceLocalHeapBytes\":268435456}"
            : string.Empty;
        return $$"""
            {"schemaVersion":1,"success":true,"loaderPath":"C:\\Windows\\System32\\vulkan-1.dll","error":null,"devices":[
              {"name":"{{name}}","apiVersion":"1.4.357","driverVersion":"2.0.349","vendorId":4098,"deviceId":0,"isTargetRadeon8060S":true,"memoryHeaps":[{"index":0,"sizeBytes":{{heapBytes}},"deviceLocal":true}],"largestDeviceLocalHeapBytes":{{heapBytes}},"totalDeviceLocalHeapBytes":{{heapBytes}}}{{extra}}
            ],"targetDeviceFound":true,"largestTargetDeviceLocalHeapBytes":{{heapBytes}},"exitCode":0}
            """;
    }
}
