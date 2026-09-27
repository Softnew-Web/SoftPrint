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

        if (kind != PaperSizeKind.Custom)
            (width, height) = PaperSizeCatalog.GetMillimeters(kind);

        return repository.Update(
            printer, simulation, paused, fit, scale,
            kind, width, height, landscape, expectedRevision);
    }

    public PrintOptions UpsertInboxEntry(InboxEntry entry, long expectedRevision)
    {
        ValidateInboxEntry(entry);

        var current = repository.Current;
        var list = current.InboxEntries.ToList();
        var idx = list.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0)
            list[idx] = entry;
        else
            list.Add(entry);

        return repository.UpdateInboxEntries(list, expectedRevision);
    }

    public PrintOptions RemoveInboxEntry(Guid id, long expectedRevision)
    {
        var current = repository.Current;
        var list = current.InboxEntries.Where(e => e.Id != id).ToList();
        return repository.UpdateInboxEntries(list, expectedRevision);
    }

    /// <summary>Substitui toda a lista de entradas (usado em importação de backup).</summary>
    public PrintOptions UpsertInboxEntries(IReadOnlyList<InboxEntry> entries, long expectedRevision)
    {
        foreach (var entry in entries)
            ValidateInboxEntry(entry);
        return repository.UpdateInboxEntries(entries, expectedRevision);
    }

    private void ValidateInboxEntry(InboxEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Folder))
            return;

        if (!string.IsNullOrWhiteSpace(entry.PrinterName) && !printers.IsInstalled(entry.PrinterName))
            throw new ArgumentException($"Impressora '{entry.PrinterName}' não está instalada. Atualize a lista e tente novamente.");

        try
        {
            var resolved = Path.GetFullPath(entry.Folder.Trim());
            Directory.CreateDirectory(resolved);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Pasta de entrada inválida: {ex.Message}");
        }
    }
}
