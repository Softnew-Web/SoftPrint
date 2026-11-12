using System.Text.Json;
using SoftPrint.Api.Contracts;
using SoftPrint.Domain;

namespace SoftPrint.Api;

public static class ApiMapping
{
    public static PrinterOptionsDto ToDto(this PrintOptions settings) =>
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
                .Select(e => new InboxEntryDto(
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
                .ToArray());

    public static PrintJobDto ToDto(this PrintJob job) =>
        new(
            job.Id,
            job.Reference,
            job.Text,
            job.Status.ToWire(),
            job.CreatedAt,
            job.FinishedAt,
            job.Error,
            job.PrinterName,
            job.SettingsRevision,
            job.Steps.Select(s => new ProcessingStepDto(s.At, s.Stage, s.Where, s.Message, s.Detail, s.IsError)).ToArray(),
            job.ErrorReason,
            job.ErrorWhere,
            job.JobType,
            job.ContentKind.ToWire(),
            job.SourcePath,
            job.TemplateName,
            job.ReprintedFromId,
            job.Priority);

    private static readonly JsonSerializerOptions BackupJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Pastas de entrada de um backup. A exportação grava em printSettings.inboxEntries;
    /// "inboxEntries" na raiz continua aceito. Null quando o backup não traz a lista.
    /// </summary>
    public static IReadOnlyList<InboxEntry>? ReadBackupInboxEntries(JsonElement root)
    {
        JsonElement list;
        if (root.TryGetProperty("inboxEntries", out var top) && top.ValueKind == JsonValueKind.Array)
            list = top;
        else if (root.TryGetProperty("printSettings", out var print) && print.ValueKind == JsonValueKind.Object
                 && print.TryGetProperty("inboxEntries", out var nested) && nested.ValueKind == JsonValueKind.Array)
            list = nested;
        else
            return null;

        return list.EnumerateArray()
            .Select(e => JsonSerializer.Deserialize<InboxEntryDto>(e.GetRawText(), BackupJson)
                         ?? throw new JsonException("Pasta de entrada vazia no backup."))
            .Select(ToDomain)
            .ToList();
    }

    public static InboxEntry ToDomain(this InboxEntryDto e)
    {
        PrintJobSettings? custom = null;
        if (e.HasCustomSettings)
        {
            var kind = PaperSizeCatalog.FromWire(e.PaperSize);
            var (pw, ph) = PaperSizeCatalog.GetMillimeters(kind, e.PaperWidthMm, e.PaperHeightMm);
            custom = new PrintJobSettings(
                ImageFitModeExtensions.FromWire(e.ImageFit),
                Math.Clamp(e.ImageScalePercent ?? 100, 10, 200),
                kind,
                kind == PaperSizeKind.Custom ? Math.Clamp(e.PaperWidthMm ?? 210, 20, 1200) : pw,
                kind == PaperSizeKind.Custom ? Math.Clamp(e.PaperHeightMm ?? 297, 20, 1200) : ph,
                e.PaperLandscape ?? false);
        }

        return new InboxEntry
        {
            Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
            Label = (e.Label ?? "").Trim(),
            PrinterName = (e.PrinterName ?? "").Trim(),
            Folder = (e.Folder ?? "").Trim(),
            Enabled = e.Enabled,
            DeleteAfterPrint = e.DeleteAfterPrint,
            CustomSettings = custom,
            Copies = Math.Clamp(e.Copies, 1, 99),
            WebhookUrl = string.IsNullOrWhiteSpace(e.WebhookUrl) ? null : e.WebhookUrl!.Trim(),
            RateLimitPerMinute = Math.Clamp(e.RateLimitPerMinute, 0, 1000)
        };
    }
}
