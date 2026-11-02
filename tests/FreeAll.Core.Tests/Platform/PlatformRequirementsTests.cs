using System.Runtime.InteropServices;
using FreeAll.Core.Platform;

namespace FreeAll.Core.Tests.Platform;

public sealed class PlatformRequirementsTests
{
    [Theory]
    [InlineData("10.0.22000.0")]
    [InlineData("10.0.22631.4460")]
    [InlineData("10.0.26100.0")]
    public void Windows_11_x64_is_supported(string version)
    {
        Assert.Equal(PlatformSupport.Supported, PlatformRequirements.Evaluate(true, Version.Parse(version), Architecture.X64));
    }

    [Theory]
    [InlineData("10.0.19045.0")]
    [InlineData("10.0.21999.0")]
    [InlineData("6.3.9600.0")]
    public void Windows_10_and_earlier_are_rejected(string version)
    {
        Assert.Equal(PlatformSupport.UnsupportedOperatingSystem, PlatformRequirements.Evaluate(true, Version.Parse(version), Architecture.X64));
    }

    [Fact]
    public void Non_windows_is_rejected()
    {
        Assert.Equal(PlatformSupport.UnsupportedOperatingSystem, PlatformRequirements.Evaluate(false, new Version(10, 0, 26100), Architecture.X64));
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm64)]
    public void Non_x64_processes_are_rejected(Architecture architecture)
    {
        Assert.Equal(PlatformSupport.UnsupportedArchitecture, PlatformRequirements.Evaluate(true, new Version(10, 0, 26100), architecture));
    }
}
