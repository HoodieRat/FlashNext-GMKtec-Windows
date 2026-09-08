using FlashNext.Core.Services;

namespace FlashNext.InstallationTests;

public sealed class VulkanHardwareParsingTests
{
    [Fact]
    public void DirectProbeJsonSelectsTargetDeviceAndDeviceLocalHeap()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "success": true,
              "loaderPath": "C:\\Windows\\System32\\vulkan-1.dll",
              "error": null,
              "devices": [
                {
                  "name": "AMD Radeon(TM) 8060S Graphics",
                  "apiVersion": "1.4.357",
                  "driverVersion": "2.0.349",
                  "vendorId": 4098,
                  "deviceId": 0,
                  "isTargetRadeon8060S": true,
                  "memoryHeaps": [
                    { "index": 0, "sizeBytes": 103079215104, "deviceLocal": true }
                  ],
                  "largestDeviceLocalHeapBytes": 103079215104,
                  "totalDeviceLocalHeapBytes": 103079215104
                }
              ],
              "targetDeviceFound": true,
              "largestTargetDeviceLocalHeapBytes": 103079215104
            }
            """;

        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.Equal("AMD Radeon(TM) 8060S Graphics", evaluation.TargetDeviceName);
        Assert.Equal("1.4.357", evaluation.ApiVersion);
        Assert.Equal(103079215104UL, evaluation.LargestTargetDeviceLocalHeapBytes);
        Assert.True(evaluation.MeetsFactoryUmaRequirement);
    }

    [Fact]
    public void InsufficientHeapMessageContainsRequiredBiosText()
    {
        const string json = """
            {"schemaVersion":1,"success":true,"loaderPath":"vulkan-1.dll","error":null,"devices":[
              {"name":"AMD Radeon 8060S","apiVersion":"1.4.0","driverVersion":"1.0.0","vendorId":4098,"deviceId":0,"isTargetRadeon8060S":true,"memoryHeaps":[{"index":0,"sizeBytes":68719476736,"deviceLocal":true}],"largestDeviceLocalHeapBytes":68719476736,"totalDeviceLocalHeapBytes":68719476736}
            ],"targetDeviceFound":true,"largestTargetDeviceLocalHeapBytes":68719476736}
            """;
        var evaluation = VulkanProbeEvaluator.Evaluate(json);
        Assert.False(evaluation.MeetsFactoryUmaRequirement);
        Assert.Equal(VulkanProbeEvaluator.BiosUmaInstruction, "Set the GMKtec EVO-X2 BIOS UMA frame buffer to 96 GB, save the BIOS setting, reboot Windows, and rerun install.cmd.");
        Assert.Contains(VulkanProbeEvaluator.BiosUmaInstruction, evaluation.FailureMessage, StringComparison.Ordinal);
    }
}
