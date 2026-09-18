using AutoPrint.Api;
using AutoPrint.Api.Contracts;
using AutoPrint.Application;
using AutoPrint.Application.Abstractions;
using AutoPrint.Application.Services;
using AutoPrint.Domain;
using AutoPrint.Infrastructure.Printing;
using Microsoft.Extensions.Options;

namespace AutoPrint.Api.Endpoints;

public static class JobEndpoints
{
    public static RouteGroupBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs");

        group.MapGet("", (JobQueueService jobs) => jobs.List().Select(j => j.ToDto()));
        group.MapGet("{id:guid}", (Guid id, JobQueueService jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(job.ToDto()) : Results.NotFound());
        group.MapPost("", (SubmitJobRequest request, JobQueueService jobs) =>
        {
            try
            {
                var result = jobs.Submit(
                    request.Reference, request.Text ?? "", request.JobType,
                    request.ContentKind, request.SourcePath, request.Template);
                return Results.Accepted($"/api/jobs/{result.Id}", result.ToDto());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        group.MapPost("{id:guid}/reprint", (Guid id, JobQueueService jobs) =>
        {
            try
            {
                var result = jobs.Reprint(id);
                return Results.Accepted($"/api/jobs/{result.Id}", result.ToDto());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return group;
    }
}

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (SettingsService settings) => settings.Current.ToDto());
        app.MapPut("/api/settings", (SettingsRequest request, SettingsService settings) =>
        {
            try
            {
                return Results.Ok(settings.Update(
                    request.PrinterName,
                    request.Simulation,
                    request.Paused,
                    request.ImageFit,
                    request.ImageScalePercent,
                    request.PaperSize,
                    request.PaperWidthMm,
                    request.PaperHeightMm,
                    request.PaperLandscape,
                    request.InboxFolder,
                    request.InboxEnabled,
                    request.DeleteInboxAfterPrint,
                    request.ExpectedRevision).ToDto());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SettingsConflictException)
            {
                return Results.Conflict(new { error = "As configurações mudaram. Recarregue antes de salvar." });
            }
        });
        app.MapGet("/api/printers", (IPrinterCatalog catalog) =>
            catalog.ListDetailed().Select(p => new PrinterDeviceDto(
                p.Name, p.Port, p.Connection, p.Driver, p.IsDefault, p.IsOffline,
                p.IsNetwork, p.IsLocal, p.IsShared, p.Status, p.DisplayLabel)));
        app.MapGet("/api/printers/names", (IPrinterCatalog catalog) => catalog.ListInstalled());
        app.MapGet("/api/printers/page-metrics", (
            string? printerName, string? paperSize, double? widthMm, double? heightMm,
            bool? landscape, SettingsService settings) =>
        {
            var current = settings.Current;
            var options = new PrintOptions
            {
                PaperSize = PaperSizeCatalog.FromWire(paperSize ?? current.PaperSize.ToWire()),
                PaperWidthMm = widthMm ?? current.PaperWidthMm,
                PaperHeightMm = heightMm ?? current.PaperHeightMm,
                PaperLandscape = landscape ?? current.PaperLandscape
            };
            return PrinterPageMetrics.Read(printerName ?? current.PrinterName, options);
        });
        app.MapPost("/api/printers/discover", async (INetworkPrinterDiscovery discovery, CancellationToken ct) =>
            await discovery.DiscoverAsync(ct));
        app.MapGet("/api/templates", (ITemplateRenderer templates) => templates.List());
        app.MapGet("/api/metrics", (IMetricsService metrics) => metrics.Compute());
        app.MapGet("/api/events", (IEventLogStore logs, DateOnly? day) =>
        {
            var target = day ?? DateOnly.FromDateTime(DateTime.Now);
            return new
            {
                day = target,
                folder = logs.FolderPath,
                days = logs.ListDays(),
                events = logs.ReadDay(target)
            };
        });
        app.MapPost("/api/events/open-folder", (IEventLogStore logs) =>
        {
            logs.OpenFolder();
            return Results.Ok(new { folder = logs.FolderPath });
        });
        app.MapGet("/api/webhook/retries", (IWebhookRetryQueue queue) => queue.Snapshot());
        app.MapGet("/api/inbox", (InboxService inbox) => inbox.Snapshot());
        app.MapPost("/api/inbox/open", (InboxService inbox) =>
        {
            try { return Results.Ok(new { folder = inbox.OpenFolder() }); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/api/inbox/browse", (InboxService inbox) =>
        {
            var folder = inbox.BrowseFolder();
            return folder is null
                ? Results.Ok(new { cancelled = true, folder = (string?)null })
                : Results.Ok(new { cancelled = false, folder });
        });
        app.MapGet("/api/status", (
            SettingsService settings,
            JobQueueService jobs,
            IWindowsStartupService startup,
            IMetricsService metrics,
            IOptions<AutoPrintFeatureOptions> features) =>
        {
            var current = settings.Current;
            var all = jobs.List();
            var m = metrics.Compute();
            return new StatusResponse(
                "AutoPrint",
                current.Simulation,
                current.PrinterName,
                current.ToDto(),
                new HealthInfo(
                    UptimeSeconds: (long)(DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                    QueuePending: all.Count(j => j.Status == JobStatus.Pending),
                    QueueProcessing: all.Count(j => j.Status == JobStatus.Processing),
                    TotalJobs: all.Count,
                    Uncertain: all.Count(j => j.Status == JobStatus.Uncertain),
                    LastError: all.Where(j => j.Status == JobStatus.Uncertain).OrderByDescending(j => j.FinishedAt).Select(j => j.Error).FirstOrDefault(),
                    StartWithWindows: startup.IsEnabled,
                    WebhookConfigured: !string.IsNullOrWhiteSpace(features.Value.WebhookUrl),
                    RetentionDays: features.Value.RetentionDays,
                    Features: new FeatureFlagsDto(
                        features.Value.SoundEnabled,
                        features.Value.DarkTheme,
                        features.Value.LogToFile,
                        features.Value.StartWithWindows),
                    Metrics: new MetricsDto(
                        m.TotalJobs, m.Last24Hours, m.JobsPerHourLast24h, m.UncertainTotal,
                        m.UncertainRatePercent, m.SimulatedTotal, m.SentTotal, m.Pending,
                        m.Processing, m.BusiestJobType)));
        });
        app.MapPost("/api/startup", (StartupRequest request, IWindowsStartupService startup) =>
        {
            startup.ApplyFromOptions(request.Enabled);
            return Results.Ok(new { startWithWindows = startup.IsEnabled });
        });

        return app;
    }
}
