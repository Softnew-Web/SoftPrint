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
        long expectedRevision);
    PrintOptions UpdateInboxEntries(IReadOnlyList<InboxEntry> entries, long expectedRevision);
}
