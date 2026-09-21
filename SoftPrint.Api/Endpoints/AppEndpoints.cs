using SoftPrint.Api;
using SoftPrint.Api.Contracts;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using Microsoft.Extensions.Options;

namespace SoftPrint.Api.Endpoints;

public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs", (JobQueueService jobs) => jobs.List().Select(j => j.ToDto()));
        app.MapGet("/api/jobs/{id:guid}", (Guid id, JobQueueService jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(job.ToDto()) : Results.NotFound());
        app.MapPost("/api/jobs", (SubmitJobRequest request, JobQueueService jobs) =>
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
        app.MapPost("/api/jobs/{id:guid}/reprint", (Guid id, JobQueueService jobs) =>
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

        return app;
    }
}

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (SettingsService settings) => settings.Current.ToDto());
        app.MapGet("/api/system-settings", (ISystemSettingsRepository settings) =>
        {
            var value = settings.Current;
            return Results.Ok(new
            {
                value.PollIntervalMs,
                value.RetentionDays,
                value.BackupIntervalMinutes,
                value.SoundEnabled,
                value.LogToFile,
                value.WebhookUrl,
                hasWebhookSecret = !string.IsNullOrWhiteSpace(value.WebhookSecret),
                value.WebhookTimeoutMs,
                value.WebhookRetrySeconds,
                value.WebhookMaxRetries,
                value.EventLogRetentionDays,
                value.NetworkScanTimeoutMs,
                value.TelemetryEnabled,
                value.TelemetryUrl,
                restartRequired = true
            });
        });
        app.MapPut("/api/system-settings", (
            SystemSettingsRequest request,
            ISystemSettingsRepository settings) =>
        {
            var current = settings.Current;
            var secret = request.WebhookSecret == "********"
                ? current.WebhookSecret
                : request.WebhookSecret ?? "";
            var value = settings.Update(new SystemSettings(
                request.PollIntervalMs,
                request.RetentionDays,
                request.BackupIntervalMinutes,
                request.SoundEnabled,
                request.LogToFile,
                request.WebhookUrl?.Trim() ?? "",
                secret,
                request.WebhookTimeoutMs,
                request.WebhookRetrySeconds,
                request.WebhookMaxRetries,
                request.EventLogRetentionDays,
                request.NetworkScanTimeoutMs,
                request.TelemetryEnabled,
                request.TelemetryUrl?.Trim() ?? ""));
            return Results.Ok(new
            {
                saved = true,
                restartRequired = true,
                value.PollIntervalMs,
                value.RetentionDays,
                value.BackupIntervalMinutes,
                value.TelemetryEnabled,
                value.TelemetryUrl
            });
        });
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
            bool? landscape, SettingsService settings, IPrinterPageMetrics metrics) =>
        {
            var current = settings.Current;
            var options = new PrintOptions
            {
                PaperSize = PaperSizeCatalog.FromWire(paperSize ?? current.PaperSize.ToWire()),
                PaperWidthMm = widthMm ?? current.PaperWidthMm,
                PaperHeightMm = heightMm ?? current.PaperHeightMm,
                PaperLandscape = landscape ?? current.PaperLandscape
            };
            return metrics.Read(printerName ?? current.PrinterName, options);
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
            HttpRequest request,
            SettingsService settings,
            JobQueueService jobs,
            IWindowsStartupService startup,
            IMetricsService metrics,
            IPlatformCapabilities capabilities,
            IUpdateChecker updates,
            IOptions<SoftPrintFeatureOptions> features) =>
        {
            var current = settings.Current;
            var all = jobs.List();
            var m = metrics.Compute();
            var baseUrl = $"{request.Scheme}://{request.Host}".TrimEnd('/');
            var application = capabilities.IsLegacy ? "SoftPrint Legacy" : "SoftPrint";
            var update = ToUpdateDto(updates.TryGetCached());
            return new StatusResponse(
                application,
                baseUrl,
                SoftPrintVersion.Current,
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
                        m.Processing, m.BusiestJobType)),
                new PlatformCapabilitiesDto(
                    capabilities.Platform,
                    capabilities.PrintingBackend,
                    capabilities.HasDesktopShell,
                    capabilities.HasNativeFolderPicker,
                    capabilities.StartupRegistration,
                    capabilities.IsLegacy),
                update);
        });
        app.MapGet("/api/update", async (HttpRequest request, IUpdateChecker updates, CancellationToken ct) =>
        {
            var refresh = request.Query.ContainsKey("refresh")
                          || string.Equals(request.Query["refresh"], "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(request.Query["refresh"], "true", StringComparison.OrdinalIgnoreCase);
            if (refresh)
                updates.InvalidateCache();

            var result = await updates.CheckAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                result.CurrentVersion,
                result.LatestVersion,
                result.UpdateAvailable,
                result.Mandatory,
                result.DownloadUrl,
                result.ReleaseUrl,
                result.ReleaseNotes,
                result.Error,
                checkedAt = result.CheckedAt
            });
        });
        app.MapGet("/api/update/progress", (IUpdateApplier applier) =>
        {
            var s = applier.GetStatus();
            return Results.Ok(new
            {
                s.Phase,
                s.Percent,
                s.Message,
                s.InProgress,
                s.Failed,
                s.Restarting,
                s.Error
            });
        });
        app.MapPost("/api/update/apply", (IUpdateApplier applier) =>
        {
            if (!applier.TryStart(out var error))
                return Results.Conflict(new { error = error ?? "Não foi possível iniciar a atualização." });
            var s = applier.GetStatus();
            return Results.Accepted("/api/update/progress", new
            {
                s.Phase,
                s.Percent,
                s.Message,
                s.InProgress,
                s.Failed,
                s.Restarting,
                s.Error
            });
        });
        app.MapPost("/api/startup", (StartupRequest request, IWindowsStartupService startup) =>
        {
            startup.ApplyFromOptions(request.Enabled);
            return Results.Ok(new { startWithWindows = startup.IsEnabled });
        });
        app.MapGet("/api/diagnose", (
            IPlatformCapabilities capabilities,
            IPrinterCatalog catalog) =>
        {
            IReadOnlyList<PrinterDeviceInfo> printers = [];
            var catalogOk = true;
            try { printers = catalog.ListDetailed(); }
            catch { catalogOk = false; }

            var extra = new[]
            {
                new DiagnoseCheck("Printer catalog", catalogOk, catalogOk ? $"{printers.Count} impressoras" : "falha ao listar"),
                new DiagnoseCheck("Desktop shell", capabilities.HasDesktopShell),
                new DiagnoseCheck("Folder picker", capabilities.HasNativeFolderPicker),
                new DiagnoseCheck("Startup registration", capabilities.StartupRegistration.Length > 0, capabilities.StartupRegistration),
                new DiagnoseCheck("Legacy edition", capabilities.IsLegacy)
            };
            var report = RuntimeDiagnostics.Create(capabilities.Platform, capabilities.PrintingBackend, extra);
            return new DiagnoseResponse(
                report.Edition,
                report.Os,
                report.Architecture,
                report.Runtime,
                report.PrintingBackend,
                printers.Count,
                new PlatformCapabilitiesDto(
                    capabilities.Platform,
                    capabilities.PrintingBackend,
                    capabilities.HasDesktopShell,
                    capabilities.HasNativeFolderPicker,
                    capabilities.StartupRegistration,
                    capabilities.IsLegacy),
                report.Checks.Select(check => new DiagnoseCheckDto(check.Name, check.Available, check.Detail)).ToArray());
        });

        return app;
    }

    private static UpdateInfoDto ToUpdateDto(UpdateCheckResult? result) =>
        result is null
            ? new UpdateInfoDto(SoftPrintVersion.Current, null, false, false, null, null, null)
            : new UpdateInfoDto(
                result.CurrentVersion,
                result.LatestVersion,
                result.UpdateAvailable,
                result.Mandatory,
                result.DownloadUrl,
                result.ReleaseUrl,
                result.Error);
}
