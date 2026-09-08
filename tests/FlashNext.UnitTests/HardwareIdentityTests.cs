using FlashNext.Core.Services;

namespace FlashNext.UnitTests;

public sealed class HardwareIdentityTests
{
    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics")]
    [InlineData("AMD Radeon 8060S Graphics")]
    [InlineData("AMD Radeon™ 8060S")]
    [InlineData("AMD RADEON™ 8060S GRAPHICS")]
    [InlineData("Radeon-8060S")]
    public void TargetGpuNamesAreAccepted(string name)
    {
        Assert.True(HardwareIdentity.IsTargetGpu(name));
    }

    [Theory]
    [InlineData("AMD Radeon 780M Graphics")]
    [InlineData("NVIDIA GeForce RTX 5080")]
    [InlineData("")]
    public void OtherGpuNamesAreRejected(string name)
    {
        Assert.False(HardwareIdentity.IsTargetGpu(name));
    }

    [Theory]
    [InlineData("AMD RYZEN AI MAX+ 395 w/ Radeon 8060S")]
    [InlineData("AMD Ryzen AI Max 395")]
    [InlineData("AMD Ryzen AI Max+ 395")]
    public void TargetCpuNamesAreAccepted(string name)
    {
        Assert.True(HardwareIdentity.IsTargetCpu(name));
    }

    [Theory]
    [InlineData("AMD Ryzen AI 9 HX 370")]
    [InlineData("AMD Ryzen 9 9950X")]
    [InlineData("")]
    public void OtherCpuNamesAreRejected(string name)
    {
        Assert.False(HardwareIdentity.IsTargetCpu(name));
    }
}
