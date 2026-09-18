using System.Runtime.InteropServices;

namespace SoftPrint.Application.Services;

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
            $"{check.Name}: {(check.Available ? "available" : "unavailable")}{(check.Detail is { Length: > 0 } detail ? $" ({detail})" : "")}")));
    }

    public static DiagnoseReport Create(
        string edition, string printingBackend, IReadOnlyList<DiagnoseCheck>? extra = null)
    {
        var checks = new List<DiagnoseCheck>
        {
            new("PDFium/Skia", TypeAvailable("PDFtoImage.Conversion, PDFtoImage")),
            new("User config root", Directory.Exists(
                Environment.GetEnvironmentVariable("SOFTPRINT_CONFIG_ROOT")
                ?? (OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftPrint")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "softprint"))))
        };
        if (extra is not null) checks.AddRange(extra);
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
        try { return Type.GetType(displayName, throwOnError: false) is not null; }
        catch { return false; }
    }
}
