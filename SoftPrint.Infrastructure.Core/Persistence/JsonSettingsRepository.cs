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

    public JsonSettingsRepository(IAppPaths paths, IConfiguration configuration, IRecentErrorLog? errorLog = null)
    {
        var directory = paths.DataRoot;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        // Arquivo corrompido não pode impedir o SoftPrint de abrir: guarda a cópia e usa o padrão.
        var loaded = DurableJsonFile.ReadOrQuarantine(
            _path,
            json => JsonSerializer.Deserialize<SettingsRecord>(json, JsonOptions) is { } record ? ToDomain(record) : null,
            message => errorLog?.Record("configurações", message));
        _current = loaded ?? new PrintOptions
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
                InboxEntries = MigrateOldInboxConfig(configuration),
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
        long expectedRevision)
    {
        lock (_gate)
        {
            if (expectedRevision != _current.Revision)
                throw new SettingsConflictException();

            var updated = _current.WithUpdate(
                printerName, simulation, paused, imageFit, imageScalePercent,
                paperSize, paperWidthMm, paperHeightMm, paperLandscape);
            Persist(updated);
            _current = updated;
            return _current;
        }
    }

    public PrintOptions UpdateInboxEntries(IReadOnlyList<InboxEntry> entries, long expectedRevision)
    {
        lock (_gate)
        {
            if (expectedRevision != _current.Revision)
                throw new SettingsConflictException();

            var updated = _current.WithInboxEntries(entries);
            Persist(updated);
            _current = updated;
            return _current;
        }
    }

    private void Persist(PrintOptions options)
    {
        DurableJsonFile.Write(_path, JsonSerializer.Serialize(ToRecord(options), JsonOptions));
    }

    private static PrintOptions ToDomain(SettingsRecord record)
    {
        var kind = PaperSizeCatalog.FromWire(record.PaperSize);
        var (presetW, presetH) = PaperSizeCatalog.GetMillimeters(kind, record.PaperWidthMm, record.PaperHeightMm);

        IReadOnlyList<InboxEntry> inboxEntries;
        if (record.InboxEntries is { Count: > 0 })
        {
            inboxEntries = record.InboxEntries
                .Select(e =>
                {
                    PrintJobSettings? customSettings = null;
                    if (e.HasCustomSettings)
                    {
                        var kind = PaperSizeCatalog.FromWire(e.PaperSize);
                        var (pw, ph) = PaperSizeCatalog.GetMillimeters(kind, e.PaperWidthMm, e.PaperHeightMm);
                        customSettings = new PrintJobSettings(
                            ImageFitModeExtensions.FromWire(e.ImageFit),
                            e.ImageScalePercent is >= 10 and <= 200 ? e.ImageScalePercent.Value : 100,
                            kind,
                            kind == PaperSizeKind.Custom ? Math.Clamp(e.PaperWidthMm ?? 210, 20, 1200) : pw,
                            kind == PaperSizeKind.Custom ? Math.Clamp(e.PaperHeightMm ?? 297, 20, 1200) : ph,
                            e.PaperLandscape ?? false);
                    }
                    return new InboxEntry
                    {
                        Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
                        Label = e.Label ?? "",
                        PrinterName = e.PrinterName ?? "",
                        Folder = e.Folder ?? "",
                        Enabled = e.Enabled,
                        DeleteAfterPrint = e.DeleteAfterPrint,
                        CustomSettings = customSettings,
                        Copies = e.Copies is >= 1 and <= 99 ? e.Copies.Value : 1,
                        WebhookUrl = string.IsNullOrWhiteSpace(e.WebhookUrl) ? null : e.WebhookUrl,
                        RateLimitPerMinute = e.RateLimitPerMinute is >= 0 and <= 1000 ? e.RateLimitPerMinute.Value : 0
                    };
                })
                .ToArray();
        }
        else
        {
            // Migração: settings.json antigo com InboxFolder único → vira primeira entrada.
            inboxEntries = MigrateOldInboxRecord(record);
        }

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
            InboxEntries = inboxEntries,
            Revision = record.Revision,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static IReadOnlyList<InboxEntry> MigrateOldInboxRecord(SettingsRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.InboxFolder))
            return [];

        return
        [
            new InboxEntry
            {
                Id = Guid.NewGuid(),
                Label = "Pasta de entrada",
                PrinterName = "",
                Folder = record.InboxFolder,
                Enabled = record.InboxEnabled ?? false,
                DeleteAfterPrint = record.DeleteInboxAfterPrint ?? false
            }
        ];
    }

    private static IReadOnlyList<InboxEntry> MigrateOldInboxConfig(IConfiguration configuration)
    {
        var folder = configuration["SoftPrint:InboxFolder"] ?? "";
        if (string.IsNullOrWhiteSpace(folder))
            return [];

        return
        [
            new InboxEntry
            {
                Id = Guid.NewGuid(),
                Label = "Pasta de entrada",
                PrinterName = "",
                Folder = folder,
                Enabled = configuration.GetValue("SoftPrint:InboxEnabled", false),
                DeleteAfterPrint = configuration.GetValue("SoftPrint:DeleteInboxAfterPrint", false)
            }
        ];
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
            settings.InboxEntries
                .Select(e => new InboxEntryRecord(
                    e.Id, e.Label, e.PrinterName, e.Folder, e.Enabled, e.DeleteAfterPrint,
                    e.CustomSettings is not null,
                    e.CustomSettings?.ImageFit.ToWire(),
                    e.CustomSettings?.ImageScalePercent,
                    e.CustomSettings?.PaperSize.ToWire(),
                    e.CustomSettings?.PaperWidthMm,
                    e.CustomSettings?.PaperHeightMm,
                    e.CustomSettings?.PaperLandscape,
                    e.Copies,
                    e.WebhookUrl,
                    e.RateLimitPerMinute))
                .ToList());

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
        List<InboxEntryRecord>? InboxEntries = null,
        // Campos legados para migração — ignorados na escrita.
        string? InboxFolder = null,
        bool? InboxEnabled = null,
        bool? DeleteInboxAfterPrint = null);

    private sealed record InboxEntryRecord(
        Guid Id,
        string? Label,
        string? PrinterName,
        string? Folder,
        bool Enabled,
        bool DeleteAfterPrint,
        bool HasCustomSettings = false,
        string? ImageFit = null,
        int? ImageScalePercent = null,
        string? PaperSize = null,
        double? PaperWidthMm = null,
        double? PaperHeightMm = null,
        bool? PaperLandscape = null,
        int? Copies = null,
        string? WebhookUrl = null,
        int? RateLimitPerMinute = null);
}
