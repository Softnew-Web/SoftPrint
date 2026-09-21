namespace SoftPrint.Domain;

public enum PaperSizeKind
{
    A4 = 0,
    A5 = 1,
    Letter = 2,
    Legal = 3,
    Photo4x6 = 4,
    Custom = 5,
    Receipt58 = 6,
    Receipt80 = 7
}

public static class PaperSizeCatalog
{
    // Largura × altura em milímetros (retrato).
    public static (double WidthMm, double HeightMm) GetMillimeters(PaperSizeKind kind, double? customW = null, double? customH = null) =>
        kind switch
        {
            PaperSizeKind.A5 => (148, 210),
            PaperSizeKind.Letter => (215.9, 279.4),
            PaperSizeKind.Legal => (215.9, 355.6),
            PaperSizeKind.Photo4x6 => (101.6, 152.4),
            PaperSizeKind.Receipt58 => (58, 200),
            PaperSizeKind.Receipt80 => (80, 297),
            PaperSizeKind.Custom => (
                Math.Clamp(customW ?? 210, 20, 1200),
                Math.Clamp(customH ?? 297, 20, 1200)),
            _ => (210, 297) // A4
        };

    public static string ToWire(this PaperSizeKind kind) => kind switch
    {
        PaperSizeKind.A5 => "a5",
        PaperSizeKind.Letter => "letter",
        PaperSizeKind.Legal => "legal",
        PaperSizeKind.Photo4x6 => "photo4x6",
        PaperSizeKind.Receipt58 => "receipt58",
        PaperSizeKind.Receipt80 => "receipt80",
        PaperSizeKind.Custom => "custom",
        _ => "a4"
    };

    public static PaperSizeKind FromWire(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "a5" => PaperSizeKind.A5,
        "letter" => PaperSizeKind.Letter,
        "legal" => PaperSizeKind.Legal,
        "photo4x6" or "4x6" => PaperSizeKind.Photo4x6,
        "receipt58" or "cupom58" or "58mm" => PaperSizeKind.Receipt58,
        "receipt80" or "cupom80" or "80mm" => PaperSizeKind.Receipt80,
        "custom" => PaperSizeKind.Custom,
        _ => PaperSizeKind.A4
    };

    public static string ToDisplay(this PaperSizeKind kind) => kind switch
    {
        PaperSizeKind.A5 => "A5 (148×210 mm)",
        PaperSizeKind.Letter => "Letter (216×279 mm)",
        PaperSizeKind.Legal => "Legal (216×356 mm)",
        PaperSizeKind.Photo4x6 => "Foto 10×15 (4×6\")",
        PaperSizeKind.Receipt58 => "Cupom 58 mm",
        PaperSizeKind.Receipt80 => "Cupom 80 mm",
        PaperSizeKind.Custom => "Personalizado",
        _ => "A4 (210×297 mm)"
    };

    /// <summary>
    /// Associa dimensões do driver a um preset conhecido (incl. cupom),
    /// ou <see cref="PaperSizeKind.Custom"/> se não houver correspondência.
    /// </summary>
    public static PaperSizeKind MatchFromMillimeters(double widthMm, double heightMm, double toleranceMm = 2.5)
    {
        var w = Math.Min(widthMm, heightMm);
        var h = Math.Max(widthMm, heightMm);

        // Cupom: prioriza a largura (altura do rolo varia muito).
        if (Near(w, 58, toleranceMm) || Near(widthMm, 58, toleranceMm) || Near(heightMm, 58, toleranceMm))
            return PaperSizeKind.Receipt58;
        if (Near(w, 80, toleranceMm) || Near(widthMm, 80, toleranceMm) || Near(heightMm, 80, toleranceMm))
            return PaperSizeKind.Receipt80;

        foreach (var kind in new[]
        {
            PaperSizeKind.A4, PaperSizeKind.A5, PaperSizeKind.Letter,
            PaperSizeKind.Legal, PaperSizeKind.Photo4x6
        })
        {
            var (pw, ph) = GetMillimeters(kind);
            if ((Near(w, pw, toleranceMm) && Near(h, ph, toleranceMm)) ||
                (Near(w, ph, toleranceMm) && Near(h, pw, toleranceMm)))
                return kind;
        }

        return PaperSizeKind.Custom;
    }

    /// <summary>Converte mm → hundredths of an inch (unidade do PrintDocument).</summary>
    public static int MmToHundredthsInch(double mm) =>
        (int)Math.Round(mm * 100.0 / 25.4);

    public static (int WidthHi, int HeightHi) ToHundredthsInch(
        PaperSizeKind kind,
        double customWidthMm,
        double customHeightMm,
        bool landscape)
    {
        var (w, h) = GetMillimeters(kind, customWidthMm, customHeightMm);
        if (landscape) (w, h) = (h, w);
        return (MmToHundredthsInch(w), MmToHundredthsInch(h));
    }

    private static bool Near(double a, double b, double tolerance) =>
        Math.Abs(a - b) <= tolerance;
}
