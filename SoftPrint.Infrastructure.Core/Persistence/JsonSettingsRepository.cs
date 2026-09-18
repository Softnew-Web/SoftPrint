using System.Text.Json;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Infrastructure.Persistence;

/// <summary>Repository: configurações com controle de concorrência otimista (revision).</summary>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    private readonly object _gate = new();
    private readonly string _path;
    private PrintOptions _current;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PrintOptions Current
    {
        get { lock (_gate) return _current; }
    }

    public JsonSettingsRepository(IAppPaths paths, IConfiguration configuration)
    {
        var directory = paths.DataRoot;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _current = File.Exists(_path)
            ? ToDomain(JsonSerializer.Deserialize<SettingsRecord>(File.ReadAllText(_path), JsonOptions)
                       ?? throw new InvalidDataException("Configuração inválida."))
            : new PrintOptions
            {
                PrinterName = configuration["SoftPrint:PrinterName"] ?? "",
                Simulation = configuration.GetValue("SoftPrint:Simulation", true),
                Paused = false,
                ImageFit = ImageFitMode.Contain,
                ImageScalePercent = 100,
                PaperSize = PaperSizeKind.A4,
                PaperWidthMm = 210,
                PaperHeightMm = 297,
                PaperLandscape = false,
                InboxFolder = configuration["SoftPrint:InboxFolder"] ?? "",
                InboxEnabled = configuration.GetValue("SoftPrint:InboxEnabled", false),
                DeleteInboxAfterPrint = configuration.GetValue("SoftPrint:DeleteInboxAfterPrint", false),
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };
    }

    public PrintOptions Update(
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
        long expectedRevision)
    {
        lock (_gate)
        {
            if (expectedRevision != _current.Revision)
                throw new SettingsConflictException();

            var updated = _current.WithUpdate(
                printerName, simulation, paused, imageFit, imageScalePercent,
                paperSize, paperWidthMm, paperHeightMm, paperLandscape,
                inboxFolder, inboxEnabled, deleteInboxAfterPrint);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(ToRecord(updated), JsonOptions));
            File.Move(_path + ".tmp", _path, true);
            _current = updated;
            return _current;
        }
    }

    private static PrintOptions ToDomain(SettingsRecord record)
    {
        var kind = PaperSizeCatalog.FromWire(record.PaperSize);
        var (presetW, presetH) = PaperSizeCatalog.GetMillimeters(kind, record.PaperWidthMm, record.PaperHeightMm);
        return new PrintOptions
        {
            PrinterName = record.PrinterName,
            Simulation = record.Simulation,
            Paused = record.Paused,
            ImageFit = ImageFitModeExtensions.FromWire(record.ImageFit),
            ImageScalePercent = record.ImageScalePercent is >= 10 and <= 200 ? record.ImageScalePercent.Value : 100,
            PaperSize = kind,
            PaperWidthMm = kind == PaperSizeKind.Custom
                ? Math.Clamp(record.PaperWidthMm ?? 210, 20, 1200)
                : presetW,
            PaperHeightMm = kind == PaperSizeKind.Custom
                ? Math.Clamp(record.PaperHeightMm ?? 297, 20, 1200)
                : presetH,
            PaperLandscape = record.PaperLandscape ?? false,
            InboxFolder = record.InboxFolder ?? "",
            InboxEnabled = record.InboxEnabled ?? false,
            DeleteInboxAfterPrint = record.DeleteInboxAfterPrint ?? false,
            Revision = record.Revision,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static SettingsRecord ToRecord(PrintOptions settings) =>
        new(
            settings.PrinterName,
            settings.Simulation,
            settings.Paused,
            settings.Revision,
            settings.UpdatedAt,
            settings.ImageFit.ToWire(),
            settings.ImageScalePercent,
            settings.PaperSize.ToWire(),
            settings.PaperWidthMm,
            settings.PaperHeightMm,
            settings.PaperLandscape,
            settings.InboxFolder,
            settings.InboxEnabled,
            settings.DeleteInboxAfterPrint);

    private sealed record SettingsRecord(
        string PrinterName,
        bool Simulation,
        bool Paused,
        long Revision,
        DateTimeOffset UpdatedAt,
        string? ImageFit = null,
        int? ImageScalePercent = null,
        string? PaperSize = null,
        double? PaperWidthMm = null,
        double? PaperHeightMm = null,
        bool? PaperLandscape = null,
        string? InboxFolder = null,
        bool? InboxEnabled = null,
        bool? DeleteInboxAfterPrint = null);
}
