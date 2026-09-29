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

    public static bool IsParseable(string raw)
    {
        return Version.TryParse(Pad(Normalize(raw)), out _);
    }

    public static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(Pad(Normalize(latest)), out var a)) return false;
        if (!Version.TryParse(Pad(Normalize(current)), out var b)) return true;
        return a > b;
    }

    /// <summary>True se <paramref name="candidate"/> for estritamente mais antiga que <paramref name="current"/>.</summary>
    public static bool IsOlder(string candidate, string current) =>
        IsNewer(current, candidate);

    private static string Pad(string version)
    {
#if NET5_0_OR_GREATER
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
#else
        var parts = version.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
#endif
        while (parts.Length < 3)
            parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(4));
    }
}
