namespace SoftPrint.Domain;

public static class SoftPrintVersionCompare
{
    public static string Normalize(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];
        var cut = s.IndexOfAny(['-', '+']);
        if (cut > 0) s = s[..cut];
        return s;
    }

    public static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(Pad(Normalize(latest)), out var a)) return false;
        if (!Version.TryParse(Pad(Normalize(current)), out var b)) return true;
        return a > b;
    }

    private static string Pad(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (parts.Length < 3)
            parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(4));
    }
}
