#if !NET5_0_OR_GREATER
// Em netcoreapp3.1, System.OperatingSystem existe mas NÃO tem IsWindows()/IsLinux()/IsMacOS().
// Este helper evita o conflito de tipos (CS0433) sem redefinir a classe do BCL.
using System.Runtime.InteropServices;

namespace SoftPrint.Infrastructure.Compat
{
    internal static class OsHelper
    {
        public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsLinux()   => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        public static bool IsMacOS()   => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        public static bool IsWindowsVersionAtLeast(int major, int minor = 0, int build = 0, int revision = 0) =>
            IsWindows() && System.Environment.OSVersion.Version >= new System.Version(major, minor, build, revision);
    }
}
#endif
