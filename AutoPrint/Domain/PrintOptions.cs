namespace AutoPrint.Domain;

public sealed class PrintOptions
{
    public string PrinterName { get; init; } = "";
    public bool Simulation { get; init; } = true;
    public bool Paused { get; init; }
    public long Revision { get; init; } = 1;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ImageFitMode ImageFit { get; init; } = ImageFitMode.Contain;
    public int ImageScalePercent { get; init; } = 100;
    public PaperSizeKind PaperSize { get; init; } = PaperSizeKind.A4;
    public double PaperWidthMm { get; init; } = 210;
    public double PaperHeightMm { get; init; } = 297;
    public bool PaperLandscape { get; init; }
    public string InboxFolder { get; init; } = "";
    public bool InboxEnabled { get; init; }
    public bool DeleteInboxAfterPrint { get; init; }

    public (double WidthMm, double HeightMm) EffectivePaperMm()
    {
        var (w, h) = PaperSizeCatalog.GetMillimeters(PaperSize, PaperWidthMm, PaperHeightMm);
        return PaperLandscape ? (h, w) : (w, h);
    }

    public PrintOptions WithUpdate(
        string printerName,
        bool simulation,
        bool paused,
        ImageFitMode imageFit,
        int imageScalePercent,
        PaperSizeKind paperSize,
        double paperWidthMm,
        double paperHeightMm,
        bool paperLandscape,
        string inboxFolder,
        bool inboxEnabled,
        bool deleteInboxAfterPrint) => new()
    {
        PrinterName = printerName,
        Simulation = simulation,
        Paused = paused,
        ImageFit = imageFit,
        ImageScalePercent = Math.Clamp(imageScalePercent, 10, 200),
        PaperSize = paperSize,
        PaperWidthMm = Math.Clamp(paperWidthMm, 20, 1200),
        PaperHeightMm = Math.Clamp(paperHeightMm, 20, 1200),
        PaperLandscape = paperLandscape,
        InboxFolder = inboxFolder.Trim(),
        InboxEnabled = inboxEnabled,
        DeleteInboxAfterPrint = deleteInboxAfterPrint,
        Revision = Revision + 1,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
