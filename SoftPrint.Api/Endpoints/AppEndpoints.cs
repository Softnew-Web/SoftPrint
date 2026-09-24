using SoftPrint.Api;
using SoftPrint.Api.Contracts;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using Microsoft.Extensions.Options;
using System.Text.Json;

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
                value.PrinterRoutes,
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
                request.TelemetryUrl?.Trim() ?? "",
                request.PrinterRoutes?.Trim() ?? current.PrinterRoutes));
            return Results.Ok(new
            {
                saved = true,
                restartRequired = true,
                value.PollIntervalMs,
                value.RetentionDays,
                value.BackupIntervalMinutes,
                value.TelemetryEnabled,
                value.TelemetryUrl,
                value.PrinterRoutes
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
        app.MapGet("/api/printers/default-paper", (
            string? printerName, SettingsService settings, IPrinterPageMetrics metrics) =>
        {
            var name = string.IsNullOrWhiteSpace(printerName) ? settings.Current.PrinterName : printerName;
            var paper = metrics.ReadDefaultPaper(name ?? "");
            return paper is null
                ? Results.NotFound(new { error = "Não foi possível ler o papel padrão desta impressora." })
                : Results.Ok(paper);
        });
        app.MapPost("/api/printers/discover", async (INetworkPrinterDiscovery discovery, CancellationToken ct) =>
            await discovery.DiscoverAsync(ct));
        app.MapPost("/api/printers/install-network", async (
            InstallNetworkPrinterRequest request,
            INetworkPrinterInstaller installer,
            CancellationToken ct) =>
        {
            var result = await installer.InstallAsync(
                request.Address ?? "",
                request.Port is > 0 and <= 65535 ? request.Port.Value : 9100,
                request.Name,
                ct).ConfigureAwait(false);
            return result.Ok
                ? Results.Ok(result)
                : Results.BadRequest(result);
        });
        app.MapPost("/api/printers/install-network-bulk", async (
            InstallNetworkPrinterBulkRequest request,
            INetworkPrinterInstaller installer,
            CancellationToken ct) =>
        {
            var items = request.Items ?? [];
            if (items.Length == 0)
                return Results.BadRequest(new { error = "Selecione ao menos uma impressora." });

            var installed = new List<object>();
            var errors = new List<object>();
            foreach (var item in items.Take(20))
            {
                var result = await installer.InstallAsync(
                    item.Address ?? "",
                    item.Port is > 0 and <= 65535 ? item.Port.Value : 9100,
                    item.Name,
                    ct).ConfigureAwait(false);
                if (result.Ok)
                    installed.Add(new { address = item.Address, port = item.Port, printerName = result.PrinterName });
                else
                    errors.Add(new { address = item.Address, port = item.Port, error = result.Error });
            }

            return Results.Ok(new { installed, errors, count = installed.Count });
        });
        app.MapGet("/api/backup/export", (
            SettingsService settings,
            ISystemSettingsRepository system,
            IApiKeyProvider keys) =>
        {
            var sys = system.Current;
            var payload = new
            {
                format = "softprint-backup-v1",
                exportedAt = DateTimeOffset.Now,
                appVersion = SoftPrintVersion.Current,
                printSettings = settings.Current.ToDto(),
                systemSettings = new
                {
                    sys.PollIntervalMs,
                    sys.RetentionDays,
                    sys.BackupIntervalMinutes,
                    sys.SoundEnabled,
                    sys.LogToFile,
                    sys.WebhookUrl,
                    hasWebhookSecret = !string.IsNullOrWhiteSpace(sys.WebhookSecret),
                    webhookSecret = string.IsNullOrWhiteSpace(sys.WebhookSecret) ? "" : "********",
                    sys.WebhookTimeoutMs,
                    sys.WebhookRetrySeconds,
                    sys.WebhookMaxRetries,
                    sys.EventLogRetentionDays,
                    sys.NetworkScanTimeoutMs,
                    sys.TelemetryEnabled,
                    sys.TelemetryUrl,
                    sys.PrinterRoutes
                },
                apiKeyHint = keys.ApiKey.Length >= 4 ? keys.ApiKey[..2] + "…" + keys.ApiKey[^2..] : "(definida)"
            };
            return Results.Json(payload);
        });
        app.MapPost("/api/backup/import", async (
            HttpRequest request,
            SettingsService settings,
            ISystemSettingsRepository system) =>
        {
            using var doc = await JsonDocument.ParseAsync(request.Body).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var format) ||
                format.GetString() is not ("softprint-backup-v1"))
                return Results.BadRequest(new { error = "Arquivo de backup inválido." });

            if (root.TryGetProperty("systemSettings", out var sysEl))
            {
                var current = system.Current;
                var secret = current.WebhookSecret;
                if (sysEl.TryGetProperty("webhookSecret", out var secEl))
                {
                    var incoming = secEl.GetString();
                    if (!string.IsNullOrWhiteSpace(incoming) && incoming != "********")
                        secret = incoming;
                }
                system.Update(new SystemSettings(
                    sysEl.TryGetProperty("pollIntervalMs", out var p) ? p.GetInt32() : current.PollIntervalMs,
                    sysEl.TryGetProperty("retentionDays", out var r) ? r.GetInt32() : current.RetentionDays,
                    sysEl.TryGetProperty("backupIntervalMinutes", out var b) ? b.GetInt32() : current.BackupIntervalMinutes,
                    !sysEl.TryGetProperty("soundEnabled", out var sound) || sound.GetBoolean(),
                    !sysEl.TryGetProperty("logToFile", out var log) || log.GetBoolean(),
                    sysEl.TryGetProperty("webhookUrl", out var wu) ? wu.GetString()?.Trim() ?? "" : current.WebhookUrl,
                    secret,
                    sysEl.TryGetProperty("webhookTimeoutMs", out var wt) ? wt.GetInt32() : current.WebhookTimeoutMs,
                    sysEl.TryGetProperty("webhookRetrySeconds", out var wr) ? wr.GetInt32() : current.WebhookRetrySeconds,
                    sysEl.TryGetProperty("webhookMaxRetries", out var wm) ? wm.GetInt32() : current.WebhookMaxRetries,
                    sysEl.TryGetProperty("eventLogRetentionDays", out var er) ? er.GetInt32() : current.EventLogRetentionDays,
                    sysEl.TryGetProperty("networkScanTimeoutMs", out var nt) ? nt.GetInt32() : current.NetworkScanTimeoutMs,
                    sysEl.TryGetProperty("telemetryEnabled", out var te) && te.GetBoolean(),
                    sysEl.TryGetProperty("telemetryUrl", out var tu) ? tu.GetString()?.Trim() ?? "" : current.TelemetryUrl,
                    sysEl.TryGetProperty("printerRoutes", out var pr) ? pr.GetString()?.Trim() ?? "" : current.PrinterRoutes));
            }

            if (root.TryGetProperty("printSettings", out var printEl))
            {
                try
                {
                    settings.Update(
                        printEl.TryGetProperty("printerName", out var pn) ? pn.GetString() : settings.Current.PrinterName,
                        printEl.TryGetProperty("simulation", out var sim) && sim.GetBoolean(),
                        printEl.TryGetProperty("paused", out var paused) && paused.GetBoolean(),
                        printEl.TryGetProperty("imageFit", out var fit) ? fit.GetString() : null,
                        printEl.TryGetProperty("imageScalePercent", out var scale) ? scale.GetInt32() : null,
                        printEl.TryGetProperty("paperSize", out var ps) ? ps.GetString() : null,
                        printEl.TryGetProperty("paperWidthMm", out var pw) ? pw.GetDouble() : null,
                        printEl.TryGetProperty("paperHeightMm", out var ph) ? ph.GetDouble() : null,
                        printEl.TryGetProperty("paperLandscape", out var pl) ? pl.GetBoolean() : null,
                        printEl.TryGetProperty("inboxFolder", out var ib) ? ib.GetString() : null,
                        printEl.TryGetProperty("inboxEnabled", out var ie) ? ie.GetBoolean() : null,
                        printEl.TryGetProperty("deleteInboxAfterPrint", out var di) ? di.GetBoolean() : null,
                        settings.Current.Revision);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = "Backup parcial: sistema ok, impressão falhou — " + ex.Message });
                }
            }

            return Results.Ok(new { imported = true });
        });
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
        app.MapGet("/api/update/rollback", (IPreviousVersionStore previous) =>
        {
            var info = previous.TryGet();
            if (info is null)
                return Results.Ok(new
                {
                    available = false,
                    currentVersion = SoftPrintVersion.Current,
                    hint = "A versão anterior só fica disponível depois de uma atualização pelo SoftPrint (guarda só a última)."
                });
            return Results.Ok(new
            {
                available = true,
                currentVersion = SoftPrintVersion.Current,
                targetVersion = info.Version,
                source = "local"
            });
        });
        app.MapGet("/api/update/history", (IUpdateHistoryStore history) =>
        {
            var h = history.Get();
            return Results.Ok(new
            {
                h.LastCheckedAt,
                h.LastCheckError,
                h.LastLatestVersion,
                h.LastUpdateAvailable,
                h.LastApplyAt,
                h.LastApplyVersion,
                h.LastApplyOk,
                h.LastApplyError,
                h.LastApplyKind,
                currentVersion = SoftPrintVersion.Current
            });
        });
        app.MapPost("/api/jobs/guided-test", (JobQueueService jobs, SettingsService settings) =>
        {
            try
            {
                var paper = settings.Current;
                var paperLabel = PrintSurfaceMapper.Describe(paper);
                var text =
                    "TESTE GUIADO SOFTPRINT\n" +
                    "----------------------\n" +
                    $"Papel: {paperLabel}\n" +
                    $"Impressora: {paper.PrinterName}\n" +
                    $"Modo: {(paper.Simulation ? "simulação" : "real")}\n" +
                    $"Quando: {DateTimeOffset.Now:HH:mm:ss}\n" +
                    "\n" +
                    "Se o papel estiver em Padrão (200×70),\n" +
                    "esta etiqueta deve caber na área imprimível.";
                var result = jobs.Submit(
                    reference: "guia-" + DateTimeOffset.Now.ToString("HHmmss"),
                    text: text,
                    jobType: "default",
                    templateName: null);
                return Results.Accepted($"/api/jobs/{result.Id}", result.ToDto());
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        app.MapPost("/api/jobs/reprint-bulk", async (HttpRequest request, JobQueueService jobs) =>
        {
            Guid[] ids;
            try
            {
                var body = await request.ReadFromJsonAsync<ReprintBulkRequest>().ConfigureAwait(false);
                ids = body?.Ids ?? [];
            }
            catch
            {
                return Results.BadRequest(new { error = "Informe a lista de pedidos." });
            }

            if (ids.Length == 0)
                return Results.BadRequest(new { error = "Selecione ao menos um pedido." });

            var accepted = new List<object>();
            var errors = new List<object>();
            foreach (var id in ids.Distinct().Take(50))
            {
                try
                {
                    var result = jobs.Reprint(id);
                    accepted.Add(new { id, newId = result.Id, reference = result.Reference });
                }
                catch (Exception ex)
                {
                    errors.Add(new { id, error = ex.Message });
                }
            }

            return Results.Ok(new { accepted, errors, count = accepted.Count });
        });
        app.MapPost("/api/update/apply", async (HttpRequest request, IUpdateApplier applier) =>
        {
            string? version = null;
            var rollback = false;
            try
            {
                if (request.ContentLength is > 0)
                {
                    var body = await request.ReadFromJsonAsync<ApplyUpdateRequest>().ConfigureAwait(false);
                    version = body?.Version;
                    rollback = body?.Rollback == true || !string.IsNullOrWhiteSpace(version);
                }
            }
            catch
            {
                /* body vazio / inválido = atualizar para latest */
            }

            var ok = rollback
                ? applier.TryStartRollback(version, out var error)
                : applier.TryStart(out error);
            if (!ok)
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
            IPrinterCatalog catalog,
            SettingsService settings,
            IApiKeyProvider keys,
            IUpdateChecker updates,
            IUpdateHistoryStore history) =>
        {
            IReadOnlyList<PrinterDeviceInfo> printers = [];
            var catalogOk = true;
            try { printers = catalog.ListDetailed(); }
            catch { catalogOk = false; }

            var opts = settings.Current;
            var selected = printers.FirstOrDefault(p =>
                string.Equals(p.Name, opts.PrinterName, StringComparison.OrdinalIgnoreCase));
            var printerOk = !string.IsNullOrWhiteSpace(opts.PrinterName) && selected is not null && !selected.IsOffline;
            var printerDetail = string.IsNullOrWhiteSpace(opts.PrinterName)
                ? "nenhuma selecionada"
                : selected is null
                    ? $"'{opts.PrinterName}' não encontrada"
                    : selected.IsOffline
                        ? $"'{opts.PrinterName}' offline"
                        : $"'{opts.PrinterName}' pronta";

            var inboxPath = opts.InboxFolder?.Trim() ?? "";
            var inboxConfigured = !string.IsNullOrWhiteSpace(inboxPath);
            var inboxExists = inboxConfigured && Directory.Exists(inboxPath);
            var inboxWritable = false;
            if (inboxExists)
            {
                try
                {
                    var probe = Path.Combine(inboxPath, ".softprint-write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    inboxWritable = true;
                }
                catch { inboxWritable = false; }
            }

            var webView2 = DetectWebView2();
            var apiKeyOk = !string.IsNullOrWhiteSpace(keys.ApiKey);
            var cachedUpdate = updates.TryGetCached();
            var hist = history.Get();
            var updateDetail = cachedUpdate?.Error
                ?? (cachedUpdate?.UpdateAvailable == true
                    ? $"nova: v{cachedUpdate.LatestVersion}"
                    : hist.LastCheckedAt is { } at
                        ? $"última verificação {at.LocalDateTime:dd/MM HH:mm}"
                        : "ainda não verificado");

            var extra = new List<DiagnoseCheck>
            {
                new("WebView2", webView2.ok, webView2.detail),
                new("Chave da API", apiKeyOk, apiKeyOk ? "configurada" : "ausente"),
                new("Pasta de entrada", !opts.InboxEnabled || inboxWritable,
                    !opts.InboxEnabled ? "vigilância desligada"
                    : !inboxConfigured ? "caminho vazio"
                    : !inboxExists ? "pasta não existe"
                    : inboxWritable ? inboxPath : "sem permissão de escrita"),
                new("Impressora selecionada", printerOk, printerDetail),
                new("Catálogo de impressoras", catalogOk, catalogOk ? $"{printers.Count} impressoras" : "falha ao listar"),
                new("Atualizações GitHub", cachedUpdate?.Error is null, updateDetail),
                new("Desktop shell", capabilities.HasDesktopShell),
                new("Seletor de pasta", capabilities.HasNativeFolderPicker),
                new("Início com Windows", capabilities.StartupRegistration.Length > 0, capabilities.StartupRegistration),
                new("Edição Legacy", capabilities.IsLegacy)
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

    private static (bool ok, string detail) DetectWebView2()
    {
        try
        {
            if (Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core") is not null)
                return (true, "assembly carregado");
        }
        catch { /* ignore */ }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var evergreen = Path.Combine(root, "Microsoft", "EdgeWebView", "Application");
            if (Directory.Exists(evergreen))
                return (true, "runtime Edge WebView2");
        }

        return (false, "runtime não encontrado — instale o WebView2");
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
