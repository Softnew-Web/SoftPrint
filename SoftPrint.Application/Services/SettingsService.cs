using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Application.Services;

/// <summary>Service: valida e aplica configurações de impressão.</summary>
public sealed class SettingsService(ISettingsRepository repository, IPrinterCatalog printers)
{
    public PrintOptions Current => repository.Current;

    public PrintOptions Update(
        string? printerName,
        bool simulation,
        bool paused,
        string? imageFit,
        int? imageScalePercent,
        string? paperSize,
        double? paperWidthMm,
        double? paperHeightMm,
        bool? paperLandscape,
        string? inboxFolder,
        bool? inboxEnabled,
        bool? deleteInboxAfterPrint,
        long expectedRevision)
    {
        var printer = printerName?.Trim() ?? "";
        if ((!simulation && string.IsNullOrWhiteSpace(printer)) ||
            (!string.IsNullOrWhiteSpace(printer) && !printers.IsInstalled(printer)))
            throw new ArgumentException("Selecione uma impressora instalada no Windows. Atualize a lista e tente novamente.");

        var current = repository.Current;
        var fit = ImageFitModeExtensions.FromWire(imageFit);
        var scale = imageScalePercent ?? current.ImageScalePercent;
        var kind = PaperSizeCatalog.FromWire(paperSize ?? current.PaperSize.ToWire());
        var width = paperWidthMm ?? current.PaperWidthMm;
        var height = paperHeightMm ?? current.PaperHeightMm;
        var landscape = paperLandscape ?? current.PaperLandscape;
        var inbox = (inboxFolder ?? current.InboxFolder)?.Trim() ?? "";
        var watch = inboxEnabled ?? current.InboxEnabled;
        var deleteAfter = deleteInboxAfterPrint ?? current.DeleteInboxAfterPrint;

        if (kind != PaperSizeKind.Custom)
            (width, height) = PaperSizeCatalog.GetMillimeters(kind);

        if (string.IsNullOrWhiteSpace(inbox))
        {
            // Limpar o caminho também desativa recursos que dependem da pasta.
            inbox = "";
            watch = false;
            deleteAfter = false;
        }
        else
        {
            try
            {
                inbox = Path.GetFullPath(inbox);
                Directory.CreateDirectory(inbox);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Pasta de entrada inválida: {ex.Message}");
            }
        }

        return repository.Update(
            printer, simulation, paused, fit, scale,
            kind, width, height, landscape,
            inbox, watch, deleteAfter, expectedRevision);
    }
}
