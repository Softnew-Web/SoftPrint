using System.Net;

namespace SoftPrint.Printing.Linux;

public static class TcpPrinterTarget
{
    public static bool TryParse(string? printerName, out string host, out int port)
    {
        host = "";
        port = 9100;
        if (string.IsNullOrWhiteSpace(printerName)) return false;

        var text = printerName.Trim();
        var explicitTcp = false;
        if (text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..];
            explicitTcp = true;
        }
        else if (text.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
            explicitTcp = true;
        }

        if (IPAddress.TryParse(text, out _))
        {
            host = text;
            return true;
        }

        var separator = text.LastIndexOf(':');
        if (separator <= 0 ||
            !int.TryParse(text[(separator + 1)..], out var parsed) ||
            parsed is <= 0 or > 65535)
        {
            return explicitTcp && text.Length > 0 && AssignHost(text, out host);
        }

        var candidate = text[..separator];
        if (IPAddress.TryParse(candidate, out _) || explicitTcp)
        {
            host = candidate;
            port = parsed;
            return host.Length > 0;
        }

        return false;
    }

    private static bool AssignHost(string text, out string host)
    {
        host = text;
        return host.Length > 0;
    }
}
