using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Printing.Linux;

public sealed class CupsPrinterCatalog(IExternalCommandRunner? runner = null) : IPrinterCatalog
{
    private readonly IExternalCommandRunner _runner = runner ?? ProcessCommandRunner.Instance;

    public IReadOnlyList<string> ListInstalled() => ReadPrinters().Select(p => p.Name).ToArray();

    public IReadOnlyList<PrinterDeviceInfo> ListDetailed()
    {
        try { return ReadPrinters(); }
        catch { return []; }
    }

    public bool IsInstalled(string printerName) =>
        ListInstalled().Contains(printerName, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PrinterDeviceInfo> ReadPrinters()
    {
        var output = _runner.Capture("lpstat", "-a");
        if (string.IsNullOrWhiteSpace(output)) return [];

        var defaultName = _runner.Capture("lpstat", "-d")
            .Split(':', 2).LastOrDefault()?.Trim() ?? "";

        return output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' ', 2)[0].Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var options = _runner.Capture("lpoptions", "-p", name);
                var uri = FindOption(options, "device-uri") ?? "cups";
                var connection = uri.StartsWith("usb", StringComparison.OrdinalIgnoreCase) ? "USB"
                    : uri.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
                      uri.Contains("ipp", StringComparison.OrdinalIgnoreCase) ? "Rede (CUPS/IPP)"
                    : "CUPS";
                return new PrinterDeviceInfo(
                    name, uri, connection, "CUPS",
                    string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    options.Contains("printer-state-reasons=offline", StringComparison.OrdinalIgnoreCase),
                    connection.Contains("Rede", StringComparison.OrdinalIgnoreCase),
                    !connection.Contains("Rede", StringComparison.OrdinalIgnoreCase),
                    false,
                    options.Contains("printer-state=3", StringComparison.OrdinalIgnoreCase) ? "idle" : "ready");
            })
            .ToArray();
    }

    private static string? FindOption(string options, string key)
    {
        foreach (var part in options.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            if (part[..separator].Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..].Trim('"');
        }
        return null;
    }
}
