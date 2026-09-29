using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SoftPrint.Api;
using SoftPrint.Api.Contracts;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Auth;
using SoftPrint.Infrastructure.Hosting;

namespace SoftPrint.Host.Windows.Legacy.Controllers
{
    [ApiController]
    public class AppController : ControllerBase
    {
        private readonly JobQueueService _jobs;
        private readonly SettingsService _settings;
        private readonly ISystemSettingsRepository _systemSettings;
        private readonly IPrinterCatalog _printerCatalog;
        private readonly IPrinterPageMetrics _printerPageMetrics;
        private readonly INetworkPrinterDiscovery _printerDiscovery;
        private readonly INetworkPrinterInstaller _printerInstaller;
        private readonly IApiKeyProvider _apiKeyProvider;
        private readonly IEventLogStore _eventLogStore;
        private readonly IWebhookRetryQueue _webhookRetryQueue;
        private readonly InboxService _inboxService;
        private readonly IUpdateChecker _updateChecker;
        private readonly IUpdateApplier _updateApplier;
        private readonly IPreviousVersionStore _previousVersionStore;
        private readonly IUpdateHistoryStore _updateHistoryStore;
        private readonly IWindowsStartupService _startupService;
        private readonly IPlatformCapabilities _capabilities;
        private readonly IMetricsService _metricsService;
        private readonly ITemplateRenderer _templates;
        private readonly IFolderOperations _folderOperations;
        private readonly ISupportBundleService _supportBundle;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IWebHostEnvironment _env;
        private readonly IOptions<SoftPrintFeatureOptions> _features;

        public AppController(
            JobQueueService jobs,
            SettingsService settings,
            ISystemSettingsRepository systemSettings,
            IPrinterCatalog printerCatalog,
            IPrinterPageMetrics printerPageMetrics,
            INetworkPrinterDiscovery printerDiscovery,
            INetworkPrinterInstaller printerInstaller,
            IApiKeyProvider apiKeyProvider,
            IEventLogStore eventLogStore,
            IWebhookRetryQueue webhookRetryQueue,
            InboxService inboxService,
            IUpdateChecker updateChecker,
            IUpdateApplier updateApplier,
            IPreviousVersionStore previousVersionStore,
            IUpdateHistoryStore updateHistoryStore,
            IWindowsStartupService startupService,
            IPlatformCapabilities capabilities,
            IMetricsService metricsService,
            ITemplateRenderer templates,
            IFolderOperations folderOperations,
            ISupportBundleService supportBundle,
            IHostApplicationLifetime lifetime,
            IWebHostEnvironment env,
            IOptions<SoftPrintFeatureOptions> features)
        {
            _jobs = jobs;
            _settings = settings;
            _systemSettings = systemSettings;
            _printerCatalog = printerCatalog;
            _printerPageMetrics = printerPageMetrics;
            _printerDiscovery = printerDiscovery;
            _printerInstaller = printerInstaller;
            _apiKeyProvider = apiKeyProvider;
            _eventLogStore = eventLogStore;
            _webhookRetryQueue = webhookRetryQueue;
            _inboxService = inboxService;
            _updateChecker = updateChecker;
            _updateApplier = updateApplier;
            _previousVersionStore = previousVersionStore;
            _updateHistoryStore = updateHistoryStore;
            _startupService = startupService;
            _capabilities = capabilities;
            _metricsService = metricsService;
            _templates = templates;
            _folderOperations = folderOperations;
            _supportBundle = supportBundle;
            _lifetime = lifetime;
            _env = env;
            _features = features;
        }

        // ── Dashboard ────────────────────────────────────────────────

        [HttpGet("/")]
        public IActionResult Index() => Redirect("/dashboard");

        [HttpGet("/dashboard")]
        public IActionResult Dashboard()
        {
            var path = System.IO.Path.Combine(_env.ContentRootPath, "wwwroot", "dashboard.html");
            if (!System.IO.File.Exists(path))
                return Content("<h1>dashboard.html não encontrado</h1>", "text/html; charset=utf-8");

            var html = System.IO.File.ReadAllText(path)
                .Replace("{{API_KEY}}", "", StringComparison.Ordinal)
                .Replace("{{APP_VERSION}}", SoftPrintVersion.Current, StringComparison.Ordinal);
            return Content(html, "text/html; charset=utf-8");
        }

        // ── Connect ───────────────────────────────────────────────────

        [HttpGet("/api/connect/key")]
        public IActionResult GetApiKey()
        {
            if (!ApiKeyMiddleware.IsLoopback(HttpContext) &&
                string.IsNullOrEmpty(Request.Headers["X-SoftPrint-Key"].ToString()))
            {
                return StatusCode(403, new { error = "Revelar a chave só é permitido em localhost ou com header." });
            }

            return Ok(new
            {
                apiKey = _apiKeyProvider.ApiKey,
                hint = _apiKeyProvider.ApiKey.Length >= 4
                    ? _apiKeyProvider.ApiKey.Substring(0, 2) + "…" + _apiKeyProvider.ApiKey.Substring(_apiKeyProvider.ApiKey.Length - 2)
                    : "(definida)"
            });
        }

        [HttpPost("/api/connect/rotate-key")]
        public IActionResult RotateKey()
        {
            var next = _apiKeyProvider.Rotate();
            if (ApiKeyMiddleware.IsLoopback(HttpContext))
                ApiKeyMiddleware.AppendSessionCookie(Response, next);

            return Ok(new
            {
                rotated = true,
                hint = next.Length >= 4 ? next.Substring(0, 2) + "…" + next.Substring(next.Length - 2) : "(definida)",
                message = "Nova chave gerada. Atualize integrações que usam a chave antiga."
            });
        }

        // ── Jobs ──────────────────────────────────────────────────────

        [HttpGet("/api/jobs")]
        public IActionResult GetJobs() => Ok(_jobs.List().Select(j => j.ToDto()));

        [HttpGet("/api/jobs/{id:guid}")]
        public IActionResult GetJob(Guid id)
        {
            var job = _jobs.Get(id);
            return job != null ? Ok(job.ToDto()) : NotFound();
        }

        [HttpPost("/api/jobs")]
        public IActionResult SubmitJob([FromBody] SubmitJobRequest request)
        {
            try
            {
                var result = _jobs.Submit(
                    request.Reference, request.Text ?? "", request.JobType,
                    request.ContentKind, request.SourcePath, request.Template);
                return Accepted($"/api/jobs/{result.Id}", result.ToDto());
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("/api/jobs/{id:guid}/reprint")]
        public IActionResult Reprint(Guid id)
        {
            try
            {
                var result = _jobs.Reprint(id);
                return Accepted($"/api/jobs/{result.Id}", result.ToDto());
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("/api/jobs/reorder")]
        public async Task<IActionResult> ReorderJobs()
        {
            try
            {
                var body = await ReadJsonAsync<ReorderRequest>().ConfigureAwait(false);
                var ids = body?.Ids ?? Array.Empty<Guid>();
                _jobs.Reorder(ids);
                return Ok(new { reordered = true, count = ids.Length });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPut("/api/jobs/{id:guid}/priority")]
        public IActionResult SetJobPriority(Guid id, [FromBody] SetPriorityRequest req)
        {
            try
            {
                _jobs.SetPriority(id, req.Priority);
                return Ok(new { id, priority = req.Priority });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("/api/jobs/guided-test")]
        public IActionResult GuidedTest()
        {
            try
            {
                var paper = _settings.Current;
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
                var result = _jobs.Submit(
                    reference: "guia-" + DateTimeOffset.Now.ToString("HHmmss"),
                    text: text,
                    jobType: "default",
                    templateName: null);
                return Accepted($"/api/jobs/{result.Id}", result.ToDto());
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("/api/jobs/reprint-bulk")]
        public async Task<IActionResult> ReprintBulk()
        {
            Guid[] ids;
            try
            {
                var body = await ReadJsonAsync<ReprintBulkRequest>().ConfigureAwait(false);
                ids = body?.Ids ?? Array.Empty<Guid>();
            }
            catch
            {
                return BadRequest(new { error = "Informe a lista de pedidos." });
            }

            if (ids.Length == 0)
                return BadRequest(new { error = "Selecione ao menos um pedido." });

            var accepted = new List<object>();
            var errors = new List<object>();
            foreach (var id in ids.Distinct().Take(50))
            {
                try
                {
                    var result = _jobs.Reprint(id);
                    accepted.Add(new { id, newId = result.Id, reference = result.Reference });
                }
                catch (Exception ex)
                {
                    errors.Add(new { id, error = ex.Message });
                }
            }

            return Ok(new { accepted, errors, count = accepted.Count });
        }

        // ── Settings ──────────────────────────────────────────────────

        [HttpGet("/api/settings")]
        public IActionResult GetSettings() => Ok(_settings.Current.ToDto());

        [HttpPut("/api/settings")]
        public IActionResult UpdateSettings([FromBody] SettingsRequest request)
        {
            try
            {
                return Ok(_settings.Update(
                    request.PrinterName,
                    request.Simulation,
                    request.Paused,
                    request.ImageFit,
                    request.ImageScalePercent,
                    request.PaperSize,
                    request.PaperWidthMm,
                    request.PaperHeightMm,
                    request.PaperLandscape,
                    request.ExpectedRevision).ToDto());
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (SettingsConflictException)
            {
                return Conflict(new { error = "As configurações mudaram. Recarregue antes de salvar." });
            }
        }

        [HttpGet("/api/system-settings")]
        public IActionResult GetSystemSettings()
        {
            var value = _systemSettings.Current;
            return Ok(new
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
        }

        [HttpPut("/api/system-settings")]
        public IActionResult UpdateSystemSettings([FromBody] SystemSettingsRequest request)
        {
            var current = _systemSettings.Current;
            var secret = request.WebhookSecret == "********"
                ? current.WebhookSecret
                : request.WebhookSecret ?? "";
            var value = _systemSettings.Update(new SystemSettings(
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
            return Ok(new
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
        }

        // ── Printers ──────────────────────────────────────────────────

        [HttpGet("/api/printers")]
        public IActionResult GetPrinters() =>
            Ok(_printerCatalog.ListDetailed().Select(p => new PrinterDeviceDto(
                p.Name, p.Port, p.Connection, p.Driver, p.IsDefault, p.IsOffline,
                p.IsNetwork, p.IsLocal, p.IsShared, p.Status, p.DisplayLabel)));

        [HttpGet("/api/printers/names")]
        public IActionResult GetPrinterNames() => Ok(_printerCatalog.ListInstalled());

        [HttpGet("/api/printers/page-metrics")]
        public IActionResult GetPageMetrics(
            [FromQuery] string printerName, [FromQuery] string paperSize,
            [FromQuery] double? widthMm, [FromQuery] double? heightMm, [FromQuery] bool? landscape)
        {
            var current = _settings.Current;
            var options = new PrintOptions
            {
                PaperSize = PaperSizeCatalog.FromWire(paperSize ?? current.PaperSize.ToWire()),
                PaperWidthMm = widthMm ?? current.PaperWidthMm,
                PaperHeightMm = heightMm ?? current.PaperHeightMm,
                PaperLandscape = landscape ?? current.PaperLandscape
            };
            return Ok(_printerPageMetrics.Read(printerName ?? current.PrinterName, options));
        }

        [HttpGet("/api/printers/default-paper")]
        public IActionResult GetDefaultPaper([FromQuery] string printerName)
        {
            var name = string.IsNullOrWhiteSpace(printerName) ? _settings.Current.PrinterName : printerName;
            var paper = _printerPageMetrics.ReadDefaultPaper(name ?? "");
            return paper is null
                ? NotFound(new { error = "Não foi possível ler o papel padrão desta impressora." })
                : Ok(paper);
        }

        [HttpPost("/api/printers/discover")]
        public async Task<IActionResult> DiscoverPrinters(CancellationToken ct) =>
            Ok(await _printerDiscovery.DiscoverAsync(ct).ConfigureAwait(false));

        [HttpPost("/api/printers/install-network")]
        public async Task<IActionResult> InstallNetworkPrinter(
            [FromBody] InstallNetworkPrinterRequest request, CancellationToken ct)
        {
            var result = await _printerInstaller.InstallAsync(
                request.Address ?? "",
                (request.Port.HasValue && request.Port.Value > 0 && request.Port.Value <= 65535) ? request.Port.Value : 9100,
                request.Name,
                ct).ConfigureAwait(false);
            return result.Ok ? Ok(result) : BadRequest(result);
        }

        [HttpPost("/api/printers/install-network-bulk")]
        public async Task<IActionResult> InstallNetworkPrinterBulk(
            [FromBody] InstallNetworkPrinterBulkRequest request, CancellationToken ct)
        {
            var items = request.Items ?? Array.Empty<InstallNetworkPrinterRequest>();
            if (items.Length == 0)
                return BadRequest(new { error = "Selecione ao menos uma impressora." });

            var installed = new List<object>();
            var errors = new List<object>();
            foreach (var item in items.Take(20))
            {
                var result = await _printerInstaller.InstallAsync(
                    item.Address ?? "",
                    (item.Port.HasValue && item.Port.Value > 0 && item.Port.Value <= 65535) ? item.Port.Value : 9100,
                    item.Name,
                    ct).ConfigureAwait(false);
                if (result.Ok)
                    installed.Add(new { address = item.Address, port = item.Port, printerName = result.PrinterName });
                else
                    errors.Add(new { address = item.Address, port = item.Port, error = result.Error });
            }

            return Ok(new { installed, errors, count = installed.Count });
        }

        // ── Backup ────────────────────────────────────────────────────

        [HttpGet("/api/backup/export")]
        public IActionResult ExportBackup()
        {
            var sys = _systemSettings.Current;
            var payload = new
            {
                format = "softprint-backup-v1",
                exportedAt = DateTimeOffset.Now,
                appVersion = SoftPrintVersion.Current,
                printSettings = _settings.Current.ToDto(),
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
                apiKeyHint = _apiKeyProvider.ApiKey.Length >= 4
                    ? _apiKeyProvider.ApiKey.Substring(0, 2) + "…" + _apiKeyProvider.ApiKey.Substring(_apiKeyProvider.ApiKey.Length - 2)
                    : "(definida)"
            };
            return new JsonResult(payload);
        }

        [HttpPost("/api/backup/import")]
        public async Task<IActionResult> ImportBackup()
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var format) ||
                format.GetString() != "softprint-backup-v1")
                return BadRequest(new { error = "Arquivo de backup inválido." });

            if (root.TryGetProperty("systemSettings", out var sysEl))
            {
                var current = _systemSettings.Current;
                var secret = current.WebhookSecret;
                if (sysEl.TryGetProperty("webhookSecret", out var secEl))
                {
                    var incoming = secEl.GetString();
                    if (!string.IsNullOrWhiteSpace(incoming) && incoming != "********")
                        secret = incoming;
                }
                _systemSettings.Update(new SystemSettings(
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
                    _settings.Update(
                        printEl.TryGetProperty("printerName", out var pn) ? pn.GetString() : _settings.Current.PrinterName,
                        printEl.TryGetProperty("simulation", out var sim) && sim.GetBoolean(),
                        printEl.TryGetProperty("paused", out var paused) && paused.GetBoolean(),
                        printEl.TryGetProperty("imageFit", out var fit) ? fit.GetString() : null,
                        printEl.TryGetProperty("imageScalePercent", out var scale) ? scale.GetInt32() : (int?)null,
                        printEl.TryGetProperty("paperSize", out var ps) ? ps.GetString() : null,
                        printEl.TryGetProperty("paperWidthMm", out var pw) ? pw.GetDouble() : (double?)null,
                        printEl.TryGetProperty("paperHeightMm", out var ph) ? ph.GetDouble() : (double?)null,
                        printEl.TryGetProperty("paperLandscape", out var pl) ? pl.GetBoolean() : (bool?)null,
                        _settings.Current.Revision);
                }
                catch (Exception ex)
                {
                    return BadRequest(new { error = "Backup parcial: sistema ok, impressão falhou — " + ex.Message });
                }
            }

            if (root.TryGetProperty("inboxEntries", out var inboxEl) && inboxEl.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    var entries = inboxEl.EnumerateArray().Select(e => new InboxEntry
                    {
                        Id = e.TryGetProperty("id", out var eid) && eid.TryGetGuid(out var g) ? g : Guid.NewGuid(),
                        Label = e.TryGetProperty("label", out var lbl) ? lbl.GetString() ?? "" : "",
                        PrinterName = e.TryGetProperty("printerName", out var epr) ? epr.GetString() ?? "" : "",
                        Folder = e.TryGetProperty("folder", out var ef) ? ef.GetString() ?? "" : "",
                        Enabled = e.TryGetProperty("enabled", out var ee) && ee.GetBoolean(),
                        DeleteAfterPrint = e.TryGetProperty("deleteAfterPrint", out var ed) && ed.GetBoolean()
                    }).ToList();
                    _settings.UpsertInboxEntries(entries, _settings.Current.Revision);
                }
                catch (Exception ex)
                {
                    return BadRequest(new { error = "Backup parcial: pastas de entrada não importadas — " + ex.Message });
                }
            }

            return Ok(new { imported = true });
        }

        // ── Templates / Metrics / Events ──────────────────────────────

        [HttpGet("/api/templates")]
        public IActionResult GetTemplates() => Ok(_templates.List());

        [HttpGet("/api/metrics")]
        public IActionResult GetMetrics() => Ok(_metricsService.Compute());

        [HttpGet("/api/events")]
        public IActionResult GetEvents([FromQuery] DateTime? day = null)
        {
            var target = day.HasValue
                ? DateOnly.FromDateTime(day.Value)
                : DateOnly.FromDateTime(DateTime.Now);
            return Ok(new
            {
                day = target,
                folder = _eventLogStore.FolderPath,
                days = _eventLogStore.ListDays(),
                events = _eventLogStore.ReadDay(target)
            });
        }

        [HttpPost("/api/events/open-folder")]
        public IActionResult OpenEventsFolder()
        {
            _eventLogStore.OpenFolder();
            return Ok(new { folder = _eventLogStore.FolderPath });
        }

        [HttpGet("/api/webhook/retries")]
        public IActionResult GetWebhookRetries() => Ok(_webhookRetryQueue.Snapshot());

        // ── Inbox ─────────────────────────────────────────────────────

        [HttpGet("/api/inbox")]
        public IActionResult GetInbox() => Ok(_inboxService.Snapshot());

        [HttpGet("/api/inbox/{id:guid}")]
        public IActionResult GetInboxEntry(Guid id)
        {
            try { return Ok(_inboxService.EntrySnapshot(id)); }
            catch (ArgumentException ex) { return NotFound(new { error = ex.Message }); }
        }

        [HttpPost("/api/inbox/entries")]
        public IActionResult CreateInboxEntry([FromBody] InboxEntryRequest request)
        {
            try
            {
                var entry = new InboxEntry
                {
                    Id = request.Id ?? Guid.NewGuid(),
                    Label = request.Label?.Trim() ?? "",
                    PrinterName = request.PrinterName?.Trim() ?? "",
                    Folder = request.Folder?.Trim() ?? "",
                    Enabled = request.Enabled,
                    DeleteAfterPrint = request.DeleteAfterPrint,
                    CustomSettings = BuildCustomSettings(request),
                    Copies = Math.Clamp(request.Copies, 1, 99),
                    WebhookUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl.Trim(),
                    RateLimitPerMinute = Math.Clamp(request.RateLimitPerMinute, 0, 1000)
                };
                return Ok(_settings.UpsertInboxEntry(entry, request.ExpectedRevision).ToDto());
            }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
            catch (SettingsConflictException) { return Conflict(new { error = "As configurações mudaram. Recarregue antes de salvar." }); }
        }

        [HttpPut("/api/inbox/entries/{id:guid}")]
        public IActionResult UpdateInboxEntry(Guid id, [FromBody] InboxEntryRequest request)
        {
            try
            {
                var entry = new InboxEntry
                {
                    Id = id,
                    Label = request.Label?.Trim() ?? "",
                    PrinterName = request.PrinterName?.Trim() ?? "",
                    Folder = request.Folder?.Trim() ?? "",
                    Enabled = request.Enabled,
                    DeleteAfterPrint = request.DeleteAfterPrint,
                    CustomSettings = BuildCustomSettings(request),
                    Copies = Math.Clamp(request.Copies, 1, 99),
                    WebhookUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl.Trim(),
                    RateLimitPerMinute = Math.Clamp(request.RateLimitPerMinute, 0, 1000)
                };
                return Ok(_settings.UpsertInboxEntry(entry, request.ExpectedRevision).ToDto());
            }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
            catch (SettingsConflictException) { return Conflict(new { error = "As configurações mudaram. Recarregue antes de salvar." }); }
        }

        [HttpDelete("/api/inbox/entries/{id:guid}")]
        public IActionResult DeleteInboxEntry(Guid id, [FromQuery] long expectedRevision)
        {
            try { return Ok(_settings.RemoveInboxEntry(id, expectedRevision).ToDto()); }
            catch (SettingsConflictException) { return Conflict(new { error = "As configurações mudaram. Recarregue antes de salvar." }); }
        }

        [HttpPost("/api/inbox/entries/{id:guid}/open")]
        public IActionResult OpenInboxFolder(Guid id)
        {
            try { return Ok(new { folder = _inboxService.OpenFolder(id) }); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("/api/inbox/entries/{id:guid}/browse")]
        public IActionResult BrowseInboxFolder(Guid id)
        {
            var folder = _inboxService.BrowseFolder(id);
            return folder is null
                ? Ok(new { cancelled = true, folder = (string)null })
                : Ok(new { cancelled = false, folder });
        }

        [HttpPost("/api/inbox/entries/browse-new")]
        public IActionResult BrowseNewFolder([FromBody] BrowseNewRequest request)
        {
            var folder = _inboxService.BrowseFolderFree(request?.Folder ?? "");
            return folder is null
                ? Ok(new { cancelled = true, folder = (string)null })
                : Ok(new { cancelled = false, folder });
        }

        // ── Status ────────────────────────────────────────────────────

        [HttpGet("/api/status")]
        public IActionResult GetStatus()
        {
            var current = _settings.Current;
            var all = _jobs.List();
            var m = _metricsService.Compute();
            var baseUrl = $"{Request.Scheme}://{Request.Host}".TrimEnd('/');
            var application = _capabilities.IsLegacy ? "SoftPrint Legacy" : "SoftPrint";
            var update = ToUpdateDto(_updateChecker.TryGetCached());
            var queued = all.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing).ToArray();
            var oldest = queued.OrderBy(j => j.CreatedAt).FirstOrDefault();
            int? oldestMinutes = oldest is null
                ? (int?)null
                : (int)Math.Max(0, (DateTimeOffset.UtcNow - oldest.CreatedAt.ToUniversalTime()).TotalMinutes);
            var stallAfter = Math.Max(1, _features.Value.QueueStallAlertMinutes);
            string queueAlert = null;
            if (current.Paused && queued.Length > 0)
                queueAlert = $"Fila pausada · {queued.Length} pedido(s) aguardando";
            else if (oldestMinutes.HasValue && oldestMinutes.Value >= stallAfter)
                queueAlert = $"Fila parada · {oldest.Reference} há {oldestMinutes} min";

            return Ok(new StatusResponse(
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
                    StartWithWindows: _startupService.IsEnabled,
                    WebhookConfigured: !string.IsNullOrWhiteSpace(_features.Value.WebhookUrl),
                    RetentionDays: _features.Value.RetentionDays,
                    Features: new FeatureFlagsDto(
                        _features.Value.SoundEnabled,
                        _features.Value.DarkTheme,
                        _features.Value.LogToFile,
                        _features.Value.StartWithWindows),
                    Metrics: new MetricsDto(
                        m.TotalJobs, m.Last24Hours, m.JobsPerHourLast24h, m.UncertainTotal,
                        m.UncertainRatePercent, m.SimulatedTotal, m.SentTotal, m.Pending,
                        m.Processing, m.BusiestJobType),
                    QueueOldestMinutes: oldestMinutes,
                    QueueAlert: queueAlert),
                new PlatformCapabilitiesDto(
                    _capabilities.Platform,
                    _capabilities.PrintingBackend,
                    _capabilities.HasDesktopShell,
                    _capabilities.HasNativeFolderPicker,
                    _capabilities.StartupRegistration,
                    _capabilities.IsLegacy),
                update));
        }

        // ── Update ────────────────────────────────────────────────────

        [HttpGet("/api/update")]
        public async Task<IActionResult> CheckUpdate(CancellationToken ct)
        {
            var refresh = Request.Query.ContainsKey("refresh")
                          || string.Equals(Request.Query["refresh"], "1", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(Request.Query["refresh"], "true", StringComparison.OrdinalIgnoreCase);
            if (refresh) _updateChecker.InvalidateCache();

            var result = await _updateChecker.CheckAsync(ct).ConfigureAwait(false);
            return Ok(new
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
        }

        [HttpGet("/api/update/progress")]
        public IActionResult GetUpdateProgress()
        {
            var s = _updateApplier.GetStatus();
            return Ok(new { s.Phase, s.Percent, s.Message, s.InProgress, s.Failed, s.Restarting, s.Error });
        }

        [HttpGet("/api/update/rollback")]
        public IActionResult GetRollbackInfo()
        {
            var info = _previousVersionStore.TryGet();
            if (info is null)
                return Ok(new
                {
                    available = false,
                    currentVersion = SoftPrintVersion.Current,
                    hint = "A versão anterior só fica disponível depois de uma atualização pelo SoftPrint (guarda só a última)."
                });
            return Ok(new
            {
                available = true,
                currentVersion = SoftPrintVersion.Current,
                targetVersion = info.Version,
                source = "local"
            });
        }

        [HttpGet("/api/update/history")]
        public IActionResult GetUpdateHistory()
        {
            var h = _updateHistoryStore.Get();
            return Ok(new
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
        }

        [HttpPost("/api/update/apply")]
        public async Task<IActionResult> ApplyUpdate()
        {
            string version = null;
            var rollback = false;
            try
            {
                if (Request.ContentLength.HasValue && Request.ContentLength.Value > 0)
                {
                    var body = await ReadJsonAsync<ApplyUpdateRequest>().ConfigureAwait(false);
                    version = body?.Version;
                    rollback = body?.Rollback == true || !string.IsNullOrWhiteSpace(version);
                }
            }
            catch { /* body vazio / inválido = atualizar para latest */ }

            var ok = rollback
                ? _updateApplier.TryStartRollback(version, out var error)
                : _updateApplier.TryStart(out error);
            if (!ok)
                return Conflict(new { error = error ?? "Não foi possível iniciar a atualização." });
            var s = _updateApplier.GetStatus();
            return Accepted("/api/update/progress", new
            {
                s.Phase,
                s.Percent,
                s.Message,
                s.InProgress,
                s.Failed,
                s.Restarting,
                s.Error
            });
        }

        // ── Startup / Uninstall / Support / Diagnose ──────────────────

        [HttpPost("/api/startup")]
        public IActionResult ConfigureStartup([FromBody] StartupRequest request)
        {
            _startupService.ApplyFromOptions(request.Enabled);
            return Ok(new { startWithWindows = _startupService.IsEnabled });
        }

        [HttpPost("/api/settings/uninstall")]
        public async Task<IActionResult> Uninstall()
        {
            // Sempre Windows neste host (netcoreapp3.1-windows). Verificação defensiva via RuntimeInformation.
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                return BadRequest(new { error = "O desinstalador automático só está disponível no Windows." });

            var opts = new UninstallRequest();
            try
            {
                if (Request.ContentLength.HasValue && Request.ContentLength.Value > 0)
                    opts = await ReadJsonAsync<UninstallRequest>().ConfigureAwait(false) ?? opts;
            }
            catch { /* body inválido = defaults */ }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var uninsPath = System.IO.Path.Combine(localAppData, "Programs", "SoftPrint", "unins000.exe");
            if (!System.IO.File.Exists(uninsPath))
                return NotFound(new { error = "Desinstalador não encontrado. Talvez o SoftPrint não tenha sido instalado via Inno Setup." });

            var configRoot = System.IO.Path.Combine(localAppData, "SoftPrint");
            var dataRoot = System.IO.Path.Combine(configRoot, "data");
#if NET5_0_OR_GREATER
            var pid = Environment.ProcessId;
#else
            var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
#endif
            var scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SoftPrintUninstall.cmd");

            var lines = new List<string>
            {
                "@echo off",
                "setlocal",
                ":waitpid",
                $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
                "if not errorlevel 1 (",
                "  timeout /t 1 /nobreak >nul",
                "  goto waitpid",
                ")",
                "taskkill /F /IM SoftPrint.exe >nul 2>nul",
                "taskkill /F /IM SoftPrint.Legacy.exe >nul 2>nul",
                "timeout /t 1 /nobreak >nul"
            };

            foreach (var sub in new[] { "previous", "backups" })
                lines.Add($"if exist \"{System.IO.Path.Combine(configRoot, sub)}\" rmdir /s /q \"{System.IO.Path.Combine(configRoot, sub)}\"");
            foreach (var f in new[] { "pending-update.json", "update-history.json", "last-update-error.txt" })
                lines.Add($"if exist \"{System.IO.Path.Combine(configRoot, f)}\" del /f /q \"{System.IO.Path.Combine(configRoot, f)}\"");

            if (!opts.KeepWebView)
                lines.Add($"if exist \"{System.IO.Path.Combine(configRoot, "WebView2")}\" rmdir /s /q \"{System.IO.Path.Combine(configRoot, "WebView2")}\"");

            if (!opts.KeepLogs)
            {
                lines.Add($"if exist \"{System.IO.Path.Combine(dataRoot, "logs")}\" rmdir /s /q \"{System.IO.Path.Combine(dataRoot, "logs")}\"");
                lines.Add($"if exist \"{System.IO.Path.Combine(configRoot, "logs")}\" rmdir /s /q \"{System.IO.Path.Combine(configRoot, "logs")}\"");
            }

            if (!opts.KeepJobs)
                lines.Add($"if exist \"{System.IO.Path.Combine(dataRoot, "jobs.json")}\" del /f /q \"{System.IO.Path.Combine(dataRoot, "jobs.json")}\"");

            if (!opts.KeepConfig)
            {
                lines.Add($"if exist \"{configRoot}\" rmdir /s /q \"{configRoot}\"");
            }
            else if (!opts.KeepJobs && !opts.KeepLogs)
            {
                lines.Add($"if exist \"{dataRoot}\" rd \"{dataRoot}\" 2>nul");
            }

            if (opts.SaveInstaller)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var destExe = System.IO.Path.Combine(desktop, "SoftPrint-Setup.exe");
                var downloadUrl = "https://github.com/Softnew-Web/SoftPrint/releases/latest/download/SoftPrint-Setup.exe";
                lines.Add($"echo Baixando instalador SoftPrint para a Area de Trabalho...");
                lines.Add($"powershell -NoProfile -NonInteractive -Command \"Invoke-WebRequest -Uri '{downloadUrl}' -OutFile '{destExe}' -UseBasicParsing\" >nul 2>nul");
                lines.Add($"if not exist \"{destExe}\" echo Falha no download do instalador >> \"%TEMP%\\softprint-uninstall-log.txt\"");
            }

            lines.Add($"\"{uninsPath}\" /VERYSILENT /NORESTART /SUPPRESSMSGBOXES");
            lines.Add("endlocal");

            System.IO.File.WriteAllLines(scriptPath, lines);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = System.IO.Path.GetTempPath()
            };
            System.Diagnostics.Process.Start(psi);

            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                _lifetime.StopApplication();
            });

            return Ok(new { ok = true });
        }

        [HttpPost("/api/support/bundle")]
        public IActionResult CreateSupportBundle()
        {
            try
            {
                var path = _supportBundle.CreateBundle();
                try { _folderOperations.Open(System.IO.Path.GetDirectoryName(path)); } catch { /* ignore */ }
                return Ok(new
                {
                    path,
                    fileName = System.IO.Path.GetFileName(path),
                    message = "Pacote de suporte gerado (logs + sessão, sem segredos)."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("/api/diagnose")]
        public IActionResult Diagnose()
        {
            IReadOnlyList<PrinterDeviceInfo> printers = Array.Empty<PrinterDeviceInfo>();
            var catalogOk = true;
            try { printers = _printerCatalog.ListDetailed(); }
            catch { catalogOk = false; }

            var opts = _settings.Current;
            var selected = printers.FirstOrDefault(p =>
                string.Equals(p.Name, opts.PrinterName, StringComparison.OrdinalIgnoreCase));
            var printerOk = !string.IsNullOrWhiteSpace(opts.PrinterName) && selected != null && !selected.IsOffline;
            var printerDetail = string.IsNullOrWhiteSpace(opts.PrinterName)
                ? "nenhuma selecionada"
                : selected is null
                    ? $"'{opts.PrinterName}' não encontrada"
                    : selected.IsOffline
                        ? $"'{opts.PrinterName}' offline"
                        : $"'{opts.PrinterName}' pronta";

            var enabledEntries = opts.InboxEntries.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Folder)).ToList();
            var inboxOk = !enabledEntries.Any() || enabledEntries.All(e =>
            {
                if (!Directory.Exists(e.Folder)) return false;
                try
                {
                    var probe = System.IO.Path.Combine(e.Folder, ".softprint-write-test");
                    System.IO.File.WriteAllText(probe, "ok");
                    System.IO.File.Delete(probe);
                    return true;
                }
                catch { return false; }
            });
            var inboxDetail = !enabledEntries.Any()
                ? $"{opts.InboxEntries.Count} entrada(s) configurada(s), nenhuma ativa"
                : inboxOk
                    ? $"{enabledEntries.Count} pasta(s) ativa(s) e graváveis"
                    : "uma ou mais pastas com problema de acesso";

            var webView2 = DetectWebView2();
            var apiKeyOk = !string.IsNullOrWhiteSpace(_apiKeyProvider.ApiKey);
            var cachedUpdate = _updateChecker.TryGetCached();
            var hist = _updateHistoryStore.Get();
            var updateDetail = cachedUpdate?.Error
                ?? (cachedUpdate?.UpdateAvailable == true
                    ? $"nova: v{cachedUpdate.LatestVersion}"
                    : hist.LastCheckedAt.HasValue
                        ? $"última verificação {hist.LastCheckedAt.Value.LocalDateTime:dd/MM HH:mm}"
                        : "ainda não verificado");

            var extra = new List<DiagnoseCheck>
            {
                new DiagnoseCheck("WebView2", webView2.ok, webView2.detail),
                new DiagnoseCheck("Chave da API", apiKeyOk, apiKeyOk ? "configurada" : "ausente"),
                new DiagnoseCheck("Pastas de entrada", inboxOk, inboxDetail),
                new DiagnoseCheck("Impressora selecionada", printerOk, printerDetail),
                new DiagnoseCheck("Catálogo de impressoras", catalogOk, catalogOk ? $"{printers.Count} impressoras" : "falha ao listar"),
                new DiagnoseCheck("Atualizações GitHub", cachedUpdate?.Error is null, updateDetail),
                new DiagnoseCheck("Desktop shell", _capabilities.HasDesktopShell),
                new DiagnoseCheck("Seletor de pasta", _capabilities.HasNativeFolderPicker),
                new DiagnoseCheck("Início com Windows", _capabilities.StartupRegistration.Length > 0, _capabilities.StartupRegistration),
                new DiagnoseCheck("Edição Legacy", _capabilities.IsLegacy)
            };
            var report = RuntimeDiagnostics.Create(_capabilities.Platform, _capabilities.PrintingBackend, extra);
            return Ok(new DiagnoseResponse(
                report.Edition,
                report.Os,
                report.Architecture,
                report.Runtime,
                report.PrintingBackend,
                printers.Count,
                new PlatformCapabilitiesDto(
                    _capabilities.Platform,
                    _capabilities.PrintingBackend,
                    _capabilities.HasDesktopShell,
                    _capabilities.HasNativeFolderPicker,
                    _capabilities.StartupRegistration,
                    _capabilities.IsLegacy),
                report.Checks.Select(check => new DiagnoseCheckDto(check.Name, check.Available, check.Detail)).ToArray()));
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static (bool ok, string detail) DetectWebView2()
        {
            try
            {
                if (Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core") != null)
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
                var evergreen = System.IO.Path.Combine(root, "Microsoft", "EdgeWebView", "Application");
                if (Directory.Exists(evergreen))
                    return (true, "runtime Edge WebView2");
            }

            return (false, "runtime não encontrado — instale o WebView2");
        }

        private static UpdateInfoDto ToUpdateDto(UpdateCheckResult result) =>
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

        private static PrintJobSettings BuildCustomSettings(InboxEntryRequest request)
        {
            if (!request.HasCustomSettings) return null;
            var kind = PaperSizeCatalog.FromWire(request.PaperSize);
            var (pw, ph) = PaperSizeCatalog.GetMillimeters(kind, request.PaperWidthMm, request.PaperHeightMm);
            return new PrintJobSettings(
                ImageFitModeExtensions.FromWire(request.ImageFit),
                Math.Clamp(request.ImageScalePercent ?? 100, 10, 200),
                kind,
                kind == PaperSizeKind.Custom ? Math.Clamp(request.PaperWidthMm ?? 210, 20, 1200) : pw,
                kind == PaperSizeKind.Custom ? Math.Clamp(request.PaperHeightMm ?? 297, 20, 1200) : ph,
                request.PaperLandscape ?? false);
        }

        private async Task<T> ReadJsonAsync<T>() where T : class
        {
            using var reader = new StreamReader(Request.Body);
            var json = await reader.ReadToEndAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
    }
}
