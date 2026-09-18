using AutoPrint.Api.Contracts;
using AutoPrint.Domain;

namespace AutoPrint.Api;

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
            settings.InboxFolder,
            settings.InboxEnabled,
            settings.DeleteInboxAfterPrint);

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
            job.ReprintedFromId);
}
