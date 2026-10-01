using System.Runtime.InteropServices;

namespace InfraLauncher.Core;

public static class PlatformInfo
{
    /// <summary>マニフェストのengine.buildsのキー（windows-x64など）。</summary>
    public static string CurrentKey => $"{Os}-{Arch}";

    private static string Os =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unknown";

    private static string Arch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var a => a.ToString().ToLowerInvariant(),
    };
}
