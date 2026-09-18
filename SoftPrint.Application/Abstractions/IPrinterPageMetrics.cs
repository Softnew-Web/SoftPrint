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

public interface IPrinterPageMetrics
{
    PrinterPageMetricsInfo Read(string printerName, PrintOptions settings);
}
