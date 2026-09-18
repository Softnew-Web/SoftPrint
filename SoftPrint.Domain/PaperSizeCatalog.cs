namespace SoftPrint.Domain;

public enum PaperSizeKind
{
    A4 = 0,
    A5 = 1,
    Letter = 2,
    Legal = 3,
    Photo4x6 = 4,
    Custom = 5
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
        PaperSizeKind.Custom => "custom",
        _ => "a4"
    };

    public static PaperSizeKind FromWire(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "a5" => PaperSizeKind.A5,
        "letter" => PaperSizeKind.Letter,
        "legal" => PaperSizeKind.Legal,
        "photo4x6" or "4x6" => PaperSizeKind.Photo4x6,
        "custom" => PaperSizeKind.Custom,
        _ => PaperSizeKind.A4
    };

    public static string ToDisplay(this PaperSizeKind kind) => kind switch
    {
        PaperSizeKind.A5 => "A5 (148×210 mm)",
        PaperSizeKind.Letter => "Letter (216×279 mm)",
        PaperSizeKind.Legal => "Legal (216×356 mm)",
        PaperSizeKind.Photo4x6 => "Foto 10×15 (4×6\")",
        PaperSizeKind.Custom => "Personalizado",
        _ => "A4 (210×297 mm)"
    };

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
}
