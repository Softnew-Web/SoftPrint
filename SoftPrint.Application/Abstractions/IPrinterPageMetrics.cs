using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public sealed record PrinterPageMetricsInfo(
    string Source,
    double PageWidthMm,
    double PageHeightMm,
    double LeftMm,
    double TopMm,
    double RightMm,
    double BottomMm);

/// <summary>Papel padrão configurado no driver da impressora (antes de aplicar SoftPrint).</summary>
public sealed record PrinterDefaultPaperInfo(
    string Source,
    double WidthMm,
    double HeightMm,
    bool Landscape,
    string? PaperName,
    string SuggestedKind);

public interface IPrinterPageMetrics
{
    PrinterPageMetricsInfo Read(string printerName, PrintOptions settings);
    PrinterDefaultPaperInfo? ReadDefaultPaper(string printerName);
}
