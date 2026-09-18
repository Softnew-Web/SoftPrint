using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface ISettingsRepository
{
    PrintOptions Current { get; }
    PrintOptions Update(
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
        bool deleteInboxAfterPrint,
        long expectedRevision);
}
