using System.Drawing.Printing;
using System.Management;
using AutoPrint.Application.Abstractions;
using AutoPrint.Domain;

namespace AutoPrint.Infrastructure.Printing;

/// <summary>
/// Lista todas as impressoras instaladas no Windows — USB, cabo (LPT/COM),
/// rede TCP/IP, WSD e compartilhadas (\\servidor\fila).
/// </summary>
public sealed class WindowsPrinterCatalog : IPrinterCatalog
{
    public IReadOnlyList<string> ListInstalled() =>
        ListDetailed().Select(p => p.Name).ToArray();

    public IReadOnlyList<PrinterDeviceInfo> ListDetailed()
    {
        try
        {
            return QueryWin32Printers();
        }
        catch
        {
            return FallbackFromInstalledPrinters();
        }
    }

    public bool IsInstalled(string printerName) =>
        !string.IsNullOrWhiteSpace(printerName) &&
        ListInstalled().Contains(printerName, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<PrinterDeviceInfo> QueryWin32Printers()
    {
        var list = new List<PrinterDeviceInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PortName, DriverName, Network, Local, Shared, WorkOffline, PrinterStatus, Default FROM Win32_Printer");
        using var results = searcher.Get();

        foreach (ManagementObject printer in results)
        {
            using (printer)
            {
                var name = printer["Name"]?.ToString()?.Trim() ?? "";
                if (name.Length == 0) continue;

                var port = printer["PortName"]?.ToString()?.Trim() ?? "";
                var driver = printer["DriverName"]?.ToString()?.Trim() ?? "";
                var isNetwork = AsBool(printer["Network"]);
                var isLocal = AsBool(printer["Local"]);
                var isShared = AsBool(printer["Shared"]);
                var offline = AsBool(printer["WorkOffline"]);
                var isDefault = AsBool(printer["Default"]);
                var statusCode = printer["PrinterStatus"] is null ? 0 : Convert.ToInt32(printer["PrinterStatus"]);

                list.Add(new PrinterDeviceInfo(
                    name,
                    port,
                    ClassifyConnection(port, isNetwork, isLocal, isShared),
                    driver,
                    isDefault,
                    offline,
                    isNetwork || LooksLikeNetworkPort(port),
                    isLocal,
                    isShared,
                    DescribeStatus(statusCode, offline)));
            }
        }

        return list
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Connection)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PrinterDeviceInfo> FallbackFromInstalledPrinters()
    {
        var names = PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
        return names.Select(name =>
        {
            var settings = new PrinterSettings { PrinterName = name };
            return new PrinterDeviceInfo(
                name,
                "",
                "Instalada",
                "",
                settings.IsDefaultPrinter,
                false,
                false,
                true,
                false,
                settings.IsValid ? "Disponível" : "Inválida");
        })
        .OrderByDescending(p => p.IsDefault)
        .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();
    }

    internal static string ClassifyConnection(string port, bool isNetwork, bool isLocal, bool isShared)
    {
        var p = port.Trim();
        if (p.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase))
            return "USB";

        if (p.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            return "Cabo (paralela)";

        if (p.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            return "Cabo (serial)";

        if (p.StartsWith("WSD", StringComparison.OrdinalIgnoreCase))
            return "Rede (WSD)";

        if (p.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            return "Rede compartilhada";

        if (LooksLikeNetworkPort(p))
            return "Rede TCP/IP";

        if (IsVirtualPort(p))
            return "Virtual";

        if (isNetwork) return "Rede";
        if (isShared) return "Compartilhada";
        if (isLocal) return "Local";
        return string.IsNullOrEmpty(p) ? "Desconhecido" : $"Porta {p}";
    }

    private static bool LooksLikeNetworkPort(string port)
    {
        if (string.IsNullOrWhiteSpace(port)) return false;
        if (port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase)) return true;
        if (port.Contains("9100", StringComparison.OrdinalIgnoreCase)) return true;
        if (port.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase)) return true;
        // Host:porta ou IPv4
        if (System.Net.IPAddress.TryParse(port.Split(':')[0], out _)) return true;
        return port.Contains('.') && !port.Contains('\\') &&
               !port.StartsWith("FILE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVirtualPort(string port) =>
        port.StartsWith("FILE", StringComparison.OrdinalIgnoreCase) ||
        port.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase) ||
        port.StartsWith("NUL", StringComparison.OrdinalIgnoreCase) ||
        port.Contains("XPS", StringComparison.OrdinalIgnoreCase) ||
        port.Contains("OneNote", StringComparison.OrdinalIgnoreCase) ||
        port.Contains("Fax", StringComparison.OrdinalIgnoreCase) ||
        port.Contains("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase);

    private static string DescribeStatus(int code, bool offline)
    {
        if (offline) return "Offline";
        return code switch
        {
            3 => "Ociosa",
            4 => "Imprimindo",
            5 => "Aquecendo",
            6 => "Parada",
            7 => "Offline",
            1 => "Outro",
            2 => "Desconhecido",
            _ => code > 0 ? $"Status {code}" : "Disponível"
        };
    }

    private static bool AsBool(object? value) =>
        value is bool b ? b :
        value is null ? false :
        Convert.ToBoolean(value);
}
