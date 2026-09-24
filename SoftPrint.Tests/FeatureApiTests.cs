using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SoftPrint.Api;
using SoftPrint.Api.Contracts;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using Xunit;

namespace SoftPrint.Tests;

/// <summary>Testes das rotas novas: backup, histórico de update e instalação em massa na rede.</summary>
public sealed class FeatureApiTests
{
    [Fact]
    public void SoftPrintFeatureOptions_ParsePrinterRoutes()
    {
        var options = new SoftPrintFeatureOptions
        {
            PrinterRoutes = "etiqueta=Zebra;cupom=EPSON TM; =skip;bad;"
        };
        var map = options.ParseRoutes();
        Assert.Equal("Zebra", map["etiqueta"]);
        Assert.Equal("EPSON TM", map["cupom"]);
        Assert.False(map.ContainsKey(""));
    }

    [Fact]
    public async Task UpdateHistory_ReturnsRecordedCheckAndApply()
    {
        var history = new MemoryUpdateHistoryStore();
        history.RecordCheck(new UpdateCheckResult(
            "1.0.0", "1.0.1", true, false, "https://dl", "https://rel", "notas", null,
            DateTimeOffset.Parse("2026-01-15T12:00:00Z")));
        history.RecordApplySucceeded("1.0.1", rollback: false);

        await using var host = await CreateHostAsync(services =>
        {
            services.AddSingleton<IUpdateHistoryStore>(history);
        });
        var client = host.GetTestClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/update/history");
        Assert.Equal("1.0.1", json.GetProperty("lastLatestVersion").GetString());
        Assert.True(json.GetProperty("lastUpdateAvailable").GetBoolean());
        Assert.Equal("1.0.1", json.GetProperty("lastApplyVersion").GetString());
        Assert.True(json.GetProperty("lastApplyOk").GetBoolean());
        Assert.Equal("update", json.GetProperty("lastApplyKind").GetString());
        Assert.Equal(SoftPrintVersion.Current, json.GetProperty("currentVersion").GetString());
    }

    [Fact]
    public async Task BackupExport_MasksWebhookSecret_AndIncludesPrinterRoutes()
    {
        var system = new MemorySystemSettingsRepository(new SystemSettings(
            200, 30, 60, true, true,
            "https://hook.example/softprint",
            "super-secret-token",
            5000, 15, 8, 2, 400,
            false, "",
            "pdf=HP Laser;etiqueta=Zebra"));
        var settings = new MemorySettingsRepository(new PrintOptions
        {
            PrinterName = "Zebra",
            Simulation = true,
            PaperSize = PaperSizeKind.Padrao,
            PaperWidthMm = 200,
            PaperHeightMm = 70
        });

        await using var host = await CreateHostAsync(services =>
        {
            services.AddSingleton<ISystemSettingsRepository>(system);
            services.AddSingleton<ISettingsRepository>(settings);
            services.AddSingleton<IPrinterCatalog>(new MemoryPrinterCatalog("Zebra"));
            services.AddSingleton<SettingsService>();
            services.AddSingleton<IApiKeyProvider>(new MemoryApiKeyProvider("abcd1234"));
        });
        var client = host.GetTestClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/backup/export");
        Assert.Equal("softprint-backup-v1", json.GetProperty("format").GetString());
        Assert.Equal("********", json.GetProperty("systemSettings").GetProperty("webhookSecret").GetString());
        Assert.True(json.GetProperty("systemSettings").GetProperty("hasWebhookSecret").GetBoolean());
        Assert.Equal(
            "pdf=HP Laser;etiqueta=Zebra",
            json.GetProperty("systemSettings").GetProperty("printerRoutes").GetString());
        Assert.Equal("Zebra", json.GetProperty("printSettings").GetProperty("printerName").GetString());
        Assert.DoesNotContain("super-secret-token", json.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupImport_RejectsInvalidFormat_AndAppliesValidPayload()
    {
        var system = new MemorySystemSettingsRepository(new SystemSettings(
            100, 14, 30, true, true, "", "", 5000, 15, 8, 2, 400));
        var settings = new MemorySettingsRepository(new PrintOptions { Simulation = true });

        await using var host = await CreateHostAsync(services =>
        {
            services.AddSingleton<ISystemSettingsRepository>(system);
            services.AddSingleton<ISettingsRepository>(settings);
            services.AddSingleton<IPrinterCatalog>(new MemoryPrinterCatalog());
            services.AddSingleton<SettingsService>();
            services.AddSingleton<IApiKeyProvider>(new MemoryApiKeyProvider("key"));
        });
        var client = host.GetTestClient();

        var bad = await client.PostAsync(
            "/api/backup/import",
            new StringContent("""{"format":"other"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await client.PostAsync(
            "/api/backup/import",
            new StringContent(
                """
                {
                  "format": "softprint-backup-v1",
                  "systemSettings": {
                    "pollIntervalMs": 250,
                    "retentionDays": 21,
                    "backupIntervalMinutes": 90,
                    "soundEnabled": false,
                    "logToFile": true,
                    "webhookUrl": "https://example/hook",
                    "webhookSecret": "********",
                    "webhookTimeoutMs": 4000,
                    "webhookRetrySeconds": 10,
                    "webhookMaxRetries": 3,
                    "eventLogRetentionDays": 5,
                    "networkScanTimeoutMs": 500,
                    "telemetryEnabled": false,
                    "telemetryUrl": "",
                    "printerRoutes": "etiqueta=X"
                  },
                  "printSettings": {
                    "printerName": "",
                    "simulation": true,
                    "paused": false,
                    "imageFit": "contain",
                    "imageScalePercent": 100,
                    "paperSize": "padrao",
                    "paperWidthMm": 200,
                    "paperHeightMm": 70,
                    "paperLandscape": false,
                    "inboxFolder": "",
                    "inboxEnabled": false,
                    "deleteInboxAfterPrint": false
                  }
                }
                """,
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(250, system.Current.PollIntervalMs);
        Assert.Equal("etiqueta=X", system.Current.PrinterRoutes);
        Assert.Equal("", system.Current.WebhookSecret);
        Assert.Equal(PaperSizeKind.Padrao, settings.Current.PaperSize);
    }

    [Fact]
    public async Task InstallNetworkBulk_RejectsEmpty_AndAggregatesResults()
    {
        var installer = new MemoryNetworkInstaller();
        await using var host = await CreateHostAsync(services =>
        {
            services.AddSingleton<INetworkPrinterInstaller>(installer);
        });
        var client = host.GetTestClient();

        var empty = await client.PostAsJsonAsync(
            "/api/printers/install-network-bulk",
            new InstallNetworkPrinterBulkRequest([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        installer.FailAddress = "10.0.0.2";
        var bulk = await client.PostAsJsonAsync(
            "/api/printers/install-network-bulk",
            new InstallNetworkPrinterBulkRequest(
            [
                new InstallNetworkPrinterRequest("10.0.0.1", 9100, "Cozinha"),
                new InstallNetworkPrinterRequest("10.0.0.2", 9100, "Balcão"),
            ]));
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        var json = await bulk.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal(1, json.GetProperty("installed").GetArrayLength());
        Assert.Equal(1, json.GetProperty("errors").GetArrayLength());
        Assert.Equal("10.0.0.1", json.GetProperty("installed")[0].GetProperty("address").GetString());
        Assert.Equal("10.0.0.2", json.GetProperty("errors")[0].GetProperty("address").GetString());
    }

    private static async Task<WebApplication> CreateHostAsync(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configure(builder.Services);
        // Defaults so routes that need DI always resolve even if a test omits one.
        if (!builder.Services.Any(d => d.ServiceType == typeof(IUpdateHistoryStore)))
            builder.Services.AddSingleton<IUpdateHistoryStore, MemoryUpdateHistoryStore>();
        if (!builder.Services.Any(d => d.ServiceType == typeof(ISystemSettingsRepository)))
            builder.Services.AddSingleton<ISystemSettingsRepository>(
                new MemorySystemSettingsRepository(new SystemSettings(
                    100, 30, 60, true, true, "", "", 5000, 15, 8, 2, 400)));
        if (!builder.Services.Any(d => d.ServiceType == typeof(ISettingsRepository)))
            builder.Services.AddSingleton<ISettingsRepository>(new MemorySettingsRepository(new PrintOptions()));
        if (!builder.Services.Any(d => d.ServiceType == typeof(IPrinterCatalog)))
            builder.Services.AddSingleton<IPrinterCatalog>(new MemoryPrinterCatalog());
        if (!builder.Services.Any(d => d.ServiceType == typeof(SettingsService)))
            builder.Services.AddSingleton<SettingsService>();
        if (!builder.Services.Any(d => d.ServiceType == typeof(IApiKeyProvider)))
            builder.Services.AddSingleton<IApiKeyProvider>(new MemoryApiKeyProvider("k"));
        if (!builder.Services.Any(d => d.ServiceType == typeof(INetworkPrinterInstaller)))
            builder.Services.AddSingleton<INetworkPrinterInstaller, MemoryNetworkInstaller>();

        var app = builder.Build();
        MapFeatureRoutes(app);
        await app.StartAsync();
        return app;
    }

    /// <summary>Espelha as rotas de AppEndpoints usadas nestes testes (sem o restante do painel).</summary>
    private static void MapFeatureRoutes(WebApplication app)
    {
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
    }

    private sealed class MemoryApiKeyProvider(string key) : IApiKeyProvider
    {
        public string ApiKey { get; } = key;
    }

    private sealed class MemoryPrinterCatalog(params string[] names) : IPrinterCatalog
    {
        private readonly HashSet<string> _names = new(names, StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> ListInstalled() => _names.ToList();
        public IReadOnlyList<PrinterDeviceInfo> ListDetailed() =>
            _names.Select(n => new PrinterDeviceInfo(
                n, "USB001", "USB", "Driver", false, false, false, true, false, "Ready")).ToList();
        public bool IsInstalled(string printerName) =>
            string.IsNullOrWhiteSpace(printerName) || _names.Contains(printerName);
    }

    private sealed class MemorySettingsRepository(PrintOptions current) : ISettingsRepository
    {
        public PrintOptions Current { get; private set; } = current;

        public PrintOptions Update(
            string printerName, bool simulation, bool paused, ImageFitMode imageFit, int imageScalePercent,
            PaperSizeKind paperSize, double paperWidthMm, double paperHeightMm, bool paperLandscape,
            string inboxFolder, bool inboxEnabled, bool deleteInboxAfterPrint, long expectedRevision)
        {
            if (expectedRevision != Current.Revision)
                throw new SettingsConflictException();
            Current = Current.WithUpdate(
                printerName, simulation, paused, imageFit, imageScalePercent,
                paperSize, paperWidthMm, paperHeightMm, paperLandscape,
                inboxFolder, inboxEnabled, deleteInboxAfterPrint);
            return Current;
        }
    }

    private sealed class MemorySystemSettingsRepository(SystemSettings current) : ISystemSettingsRepository
    {
        public SystemSettings Current { get; private set; } = current;
        public SystemSettings Update(SystemSettings settings)
        {
            Current = settings;
            return Current;
        }
    }

    private sealed class MemoryUpdateHistoryStore : IUpdateHistoryStore
    {
        private UpdateHistorySnapshot _current = new(null, null, null, null, null, null, null, null, null);
        public UpdateHistorySnapshot Get() => _current;
        public void RecordCheck(UpdateCheckResult result) =>
            _current = _current with
            {
                LastCheckedAt = result.CheckedAt,
                LastCheckError = result.Error,
                LastLatestVersion = result.LatestVersion,
                LastUpdateAvailable = result.Error is null ? result.UpdateAvailable : null
            };
        public void RecordApplyStarted(string version, bool rollback) =>
            RecordApply(version, rollback, null, null);
        public void RecordApplyFailed(string version, bool rollback, string error) =>
            RecordApply(version, rollback, false, error);
        public void RecordApplySucceeded(string version, bool rollback) =>
            RecordApply(version, rollback, true, null);
        private void RecordApply(string version, bool rollback, bool? ok, string? error) =>
            _current = _current with
            {
                LastApplyAt = DateTimeOffset.UtcNow,
                LastApplyVersion = version,
                LastApplyOk = ok,
                LastApplyError = error,
                LastApplyKind = rollback ? "rollback" : "update"
            };
    }

    private sealed class MemoryNetworkInstaller : INetworkPrinterInstaller
    {
        public string? FailAddress { get; set; }

        public Task<NetworkPrinterInstallResult> InstallAsync(
            string address, int port = 9100, string? displayName = null,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(address, FailAddress, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new NetworkPrinterInstallResult(
                    false, "", "", "", "falha simulada"));
            }

            var name = displayName ?? $"TCP_{address}_{port}";
            return Task.FromResult(new NetworkPrinterInstallResult(
                true, name, $"IP_{address}_{port}", "Generic", null));
        }
    }
}
