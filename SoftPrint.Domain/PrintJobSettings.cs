namespace SoftPrint.Domain;

public sealed record PrintJobSettings(
    ImageFitMode ImageFit,
    int ImageScalePercent,
    PaperSizeKind PaperSize,
    double PaperWidthMm,
    double PaperHeightMm,
    bool PaperLandscape
);
