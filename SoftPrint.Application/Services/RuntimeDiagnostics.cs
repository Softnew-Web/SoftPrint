using System.Runtime.InteropServices;

namespace SoftPrint.Application.Services;

#if !NET5_0_OR_GREATER
// Alias local para RuntimeInformation helpers usados no polyfill de OperatingSystem
// (netcoreapp3.1 tem System.OperatingSystem mas sem IsWindows/IsLinux/etc.)
internal static class OsHelper
{
    public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
#endif

public sealed record DiagnoseCheck(string Name, bool Available, string? Detail = null);

public sealed record DiagnoseReport(
    string Edition,
    string Os,
    string Architecture,
    string Runtime,
    string PrintingBackend,
    IReadOnlyList<DiagnoseCheck> Checks);

public static class RuntimeDiagnostics
{
    public static string Report(string edition, string printingBackend, params (string Name, bool Available)[] checks)
    {
        var report = Create(edition, printingBackend, checks.Select(check =>
            new DiagnoseCheck(check.Name, check.Available)).ToArray());
        return string.Join(Environment.NewLine, new[]
        {
            $"SoftPrint edition: {report.Edition}",
            $"OS: {report.Os}",
            $"Architecture: {report.Architecture}",
            $"Runtime: {report.Runtime}",
            $"Printing backend: {report.PrintingBackend}"
        }.Concat(report.Checks.Select(check =>
            $"{check.Name}: {(check.Available ? "available" : "unavailable")}{(!string.IsNullOrEmpty(check.Detail) ? $" ({check.Detail})" : "")}")));
    }

    public static DiagnoseReport Create(
        string edition, string printingBackend, IReadOnlyList<DiagnoseCheck>? extra = null)
    {
        var checks = new List<DiagnoseCheck>
        {
            new("PDFium/Skia", TypeAvailable("PDFtoImage.Conversion, PDFtoImage")),
            new("User config root", Directory.Exists(
                Environment.GetEnvironmentVariable("SOFTPRINT_CONFIG_ROOT")
#if NET5_0_OR_GREATER
                ?? (OperatingSystem.IsWindows()
#else
                ?? (OsHelper.IsWindows()
#endif
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftPrint")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "softprint"))))
        };
        if (extra != null) checks.AddRange(extra);
        return new DiagnoseReport(
            edition,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            printingBackend,
            checks);
    }

    private static bool TypeAvailable(string displayName)
    {
        try { return Type.GetType(displayName, throwOnError: false) != null; }
        catch { return false; }
    }
}
