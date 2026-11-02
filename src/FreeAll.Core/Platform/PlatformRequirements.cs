using System.Runtime.InteropServices;

namespace FreeAll.Core.Platform;

public enum PlatformSupport
{
    Supported,
    UnsupportedOperatingSystem,
    UnsupportedArchitecture,
}

/// <summary>
/// Runtime platform gate: Windows 11 only (DEC-001) and x64 processes only (DEC-004, DEC-006).
/// The application manifest cannot distinguish Windows 10 from Windows 11 (TD-006).
/// </summary>
public static class PlatformRequirements
{
    /// <summary>The first Windows 11 build.</summary>
    public const int MinimumWindowsBuild = 22000;

    public static PlatformSupport Evaluate(bool isWindows, Version osVersion, Architecture processArchitecture)
    {
        ArgumentNullException.ThrowIfNull(osVersion);

        if (!isWindows || osVersion.Major < 10 || (osVersion.Major == 10 && osVersion.Build < MinimumWindowsBuild))
        {
            return PlatformSupport.UnsupportedOperatingSystem;
        }

        return processArchitecture == Architecture.X64
            ? PlatformSupport.Supported
            : PlatformSupport.UnsupportedArchitecture;
    }

    public static PlatformSupport EvaluateCurrent() =>
        Evaluate(OperatingSystem.IsWindows(), Environment.OSVersion.Version, RuntimeInformation.ProcessArchitecture);
}
