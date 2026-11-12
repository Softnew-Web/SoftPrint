using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SoftPrint.Api;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Application.Workers;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Infrastructure.Persistence;
using Xunit;

namespace SoftPrint.Tests;

public sealed class BugfixRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softprint-tests-" + Guid.NewGuid().ToString("N"));

    public BugfixRegressionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task InboxWatcher_DoesNotReprintKeptFile_UntilItChanges()
    {
        var inbox = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(inbox);
        var file = Path.Combine(inbox, "cupom.png");
        File.WriteAllBytes(file, [1, 2, 3]);

        var settings = new FixedSettings(new PrintOptions
        {
            InboxEntries = [new InboxEntry { Label = "t", Folder = inbox, Enabled = true, DeleteAfterPrint = false }]
        });
        var repo = new JsonJobRepository(new Paths(Path.Combine(_root, "data")));
        var queue = new JobQueueService(repo, new PrintJobFactory(), new TemplateRenderer(Options.Create(new SoftPrintFeatureOptions())));
        var watcher = new InboxFolderWatcher(queue, settings, new NoErrors(), NullLogger<InboxFolderWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);
        try
        {
            // Faz o papel do PrintWorker: tudo que entra na fila sai como impresso.
            await DrainFor(repo, TimeSpan.FromSeconds(4.5));
            Assert.Single(repo.Snapshot());

            // Arquivo regravado depois da impressão: é um pedido novo.
            File.WriteAllBytes(file, [4, 5, 6]);
            await DrainFor(repo, TimeSpan.FromSeconds(3.5));
            Assert.Equal(2, repo.Snapshot().Count);
        }
        finally
        {
            await watcher.StopAsync(CancellationToken.None);
        }
    }

    private static async Task DrainFor(JsonJobRepository repo, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            while (repo.PeekNextPending() is { } next && repo.TakePending(next.Id, "X", 1) is { } taken)
                repo.Finish(taken.Id, JobStatus.Sent);
            await Task.Delay(200);
        }
    }

    [Fact]
    public void SettingsRepository_PersistsCopiesWebhookAndRateLimit()
    {
        var paths = new Paths(_root);
        var config = new ConfigurationBuilder().Build();
        var first = new JsonSettingsRepository(paths, config);
        var entry = new InboxEntry
        {
            Label = "Balcão", Folder = Path.Combine(_root, "in"), Enabled = true,
            Copies = 3, WebhookUrl = "http://127.0.0.1:9/hook", RateLimitPerMinute = 12
        };
        first.UpdateInboxEntries([entry], first.Current.Revision);

        var reloaded = new JsonSettingsRepository(paths, config).Current.InboxEntries.Single();
        Assert.Equal(3, reloaded.Copies);
        Assert.Equal("http://127.0.0.1:9/hook", reloaded.WebhookUrl);
        Assert.Equal(12, reloaded.RateLimitPerMinute);
    }

    [Fact]
    public void Backup_ReadsInboxEntriesFromExportedPrintSettings()
    {
        var options = new PrintOptions
        {
            InboxEntries =
            [
                new InboxEntry
                {
                    Label = "Etiquetas", Folder = @"C:\SoftPrint\in", Enabled = true, DeleteAfterPrint = true,
                    CustomSettings = new PrintJobSettings(ImageFitMode.Contain, 90, PaperSizeKind.Custom, 58, 40, false),
                    Copies = 2, WebhookUrl = "http://127.0.0.1:9/hook", RateLimitPerMinute = 5
                }
            ]
        };
        // Mesmo formato que /api/backup/export devolve (camelCase).
        var exported = JsonSerializer.Serialize(new
        {
            format = "softprint-backup-v1",
            printSettings = options.ToDto()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var doc = JsonDocument.Parse(exported);
        var entry = Assert.Single(ApiMapping.ReadBackupInboxEntries(doc.RootElement)!);

        var original = options.InboxEntries[0];
        Assert.Equal(original.Id, entry.Id);
        Assert.Equal("Etiquetas", entry.Label);
        Assert.True(entry.DeleteAfterPrint);
        Assert.Equal(2, entry.Copies);
        Assert.Equal(5, entry.RateLimitPerMinute);
        Assert.Equal("http://127.0.0.1:9/hook", entry.WebhookUrl);
        Assert.NotNull(entry.CustomSettings);
        Assert.Equal(58, entry.CustomSettings!.PaperWidthMm);
        Assert.Equal(90, entry.CustomSettings.ImageScalePercent);
    }

    [Fact]
    public void Reprint_KeepsRequestedPrinterAndSettings()
    {
        var repo = new JsonJobRepository(new Paths(_root));
        var queue = new JobQueueService(repo, new PrintJobFactory(), new TemplateRenderer(Options.Create(new SoftPrintFeatureOptions())));
        var custom = new PrintJobSettings(ImageFitMode.Contain, 100, PaperSizeKind.Custom, 80, 120, false);
        var original = queue.Submit("ref-1", "texto", requestedPrinterName: "Termica Balcao", settingsOverride: custom);
        var taken = repo.TakePending(original.Id, "Termica Balcao", 1)!;
        repo.Finish(taken.Id, JobStatus.Sent);

        var copy = queue.Reprint(original.Id);

        Assert.Equal("Termica Balcao", copy.RequestedPrinterName);
        Assert.Equal(custom, copy.SettingsOverride);
    }

    [Fact]
    public void TakePending_TakesOnlyTheRequestedJob()
    {
        var repo = new JsonJobRepository(new Paths(_root));
        var first = repo.Add(PrintJob.CreatePending("a", "a"));
        var urgent = repo.Add(PrintJob.CreatePending("b", "b", priority: 1));

        var taken = repo.TakePending(first.Id, "P", 1);

        Assert.Equal(first.Id, taken!.Id);
        Assert.Equal(JobStatus.Pending, repo.FindById(urgent.Id)!.Status);
        Assert.Null(repo.TakePending(first.Id, "P", 1));
    }

    [Fact]
    public async Task PrintTimeout_StopsWaitingForBlockingStrategy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PrintWorker.ExecuteWithTimeoutAsync(
                new BlockingStrategy(), PrintJob.CreatePending("t", "t"), new PrintOptions(), cts.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"esperou {watch.Elapsed}");
    }

    [Theory]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("127.0.0.1:5178", HttpStatusCode.OK)]
    [InlineData("evil.example:5178", HttpStatusCode.BadRequest)]
    public async Task HostHeader_OnlyLoopbackNamesAreAccepted(string host, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        LoopbackBindingGuard.RestrictHostHeader(builder.Services);
        await using var app = builder.Build();
        app.MapGet("/dashboard", () => "ok");
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
        request.Headers.Host = host;
        using var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public void JobRepository_CorruptFile_OpensEmptyAndKeepsCopy()
    {
        File.WriteAllBytes(Path.Combine(_root, "jobs.json"), new byte[512]); // zeros, como após queda de energia
        var errors = new CapturedErrors();

        var repo = new JsonJobRepository(new Paths(_root), errors);

        Assert.Empty(repo.Snapshot());
        Assert.Single(Directory.GetFiles(_root, "jobs.json.corrompido-*"));
        Assert.Contains(errors.Messages, m => m.Contains("jobs.json", StringComparison.Ordinal));
        // Continua funcionando e grava um arquivo válido.
        repo.Add(PrintJob.CreatePending("depois", "ok"));
        Assert.Single(new JsonJobRepository(new Paths(_root)).Snapshot());
    }

    [Fact]
    public void SettingsRepository_CorruptFile_OpensWithDefaults()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{ \"printerName\": ");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SoftPrint:PrinterName"] = "Padrao" })
            .Build();

        var repo = new JsonSettingsRepository(new Paths(_root), config, new CapturedErrors());

        Assert.Equal("Padrao", repo.Current.PrinterName);
        Assert.Single(Directory.GetFiles(_root, "settings.json.corrompido-*"));
    }

    [Fact]
    public void UpdateScript_UsesRealInstallFolder_AndElevatedCopyDoesNotStartApp()
    {
        var install = Path.Combine(_root, "Program Files", "SoftPrint");
        var payload = Path.Combine(_root, "payload");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(payload);
        var script = Path.Combine(_root, "apply.cmd");

        // Pasta gravável: copia e reabre o app no mesmo script.
        var inline = new SoftPrint.Infrastructure.Updates.SoftPrintUpdateApplier.LaunchPlan(install, _root, Elevate: false);
        SoftPrint.Infrastructure.Updates.SoftPrintUpdateApplier.WriteZipRestartScript(script, payload, inline);
        var text = File.ReadAllText(script);
        Assert.Contains($"robocopy \"{payload}\" \"{install}\"", text, StringComparison.Ordinal);
        Assert.Contains($"start \"\" \"{Path.Combine(install, "SoftPrint.exe")}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Programs\SoftPrint", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%TEMP%", text, StringComparison.Ordinal);

        // Precisa de UAC: o script elevado só copia e avisa; quem reabre é o script sem elevação.
        var elevated = inline with { Elevate = true };
        SoftPrint.Infrastructure.Updates.SoftPrintUpdateApplier.WriteZipRestartScript(script, payload, elevated);
        text = File.ReadAllText(script);
        Assert.Contains($"robocopy \"{payload}\" \"{install}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("start \"\"", text, StringComparison.Ordinal);
        Assert.Contains(elevated.DoneMarker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallLocation_CanWrite_DetectsWritableFolder()
    {
        Assert.True(SoftPrintInstallLocation.CanWrite(_root));
        Assert.False(SoftPrintInstallLocation.CanWrite(Path.Combine(_root, "nao-existe")));
        Assert.Empty(Directory.GetFiles(_root, ".softprint-write-test-*"));
    }

    [Fact]
    public void ApiKeyAcl_FindsProfileOwner_ForServiceMode()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Pasta criada pelo usuário: é ele quem precisa continuar lendo a chave quando o SYSTEM a protege.
        var owner = FileApiKeyProvider.FindProfileOwner(_root);
        Assert.Equal(System.Security.Principal.WindowsIdentity.GetCurrent().User, owner);

        _ = new FileApiKeyProvider(new Paths(_root));
        Assert.True(File.ReadAllText(Path.Combine(_root, "api-key.txt")).Trim().Length >= 32);
    }

    private sealed class CapturedErrors : IRecentErrorLog
    {
        public List<string> Messages { get; } = [];
        public void Record(string source, string message) => Messages.Add(message);
        public void Clear() => Messages.Clear();
        public IReadOnlyList<RecentError> GetRecent(int max = 30) => [];
    }

    private sealed class BlockingStrategy : IPrintStrategy
    {
        public bool CanHandle(PrintJob job, PrintOptions settings) => true;

        // Igual ao PrintDocument.Print(): síncrono e ignora o token.
        public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
        {
            Thread.Sleep(TimeSpan.FromSeconds(10));
            return Task.FromResult(JobStatus.Sent);
        }
    }

    private sealed record Paths(string Root) : IAppPaths
    {
        public string DataRoot => Root;
        public string ConfigRoot => Root;
    }

    private sealed class NoErrors : IRecentErrorLog
    {
        public void Record(string source, string message) { }
        public void Clear() { }
        public IReadOnlyList<RecentError> GetRecent(int max = 30) => [];
    }

    private sealed class FixedSettings(PrintOptions options) : ISettingsRepository
    {
        public PrintOptions Current => options;

        public PrintOptions Update(
            string printerName, bool simulation, bool paused, ImageFitMode imageFit, int imageScalePercent,
            PaperSizeKind paperSize, double paperWidthMm, double paperHeightMm, bool paperLandscape,
            long expectedRevision) => options;

        public PrintOptions UpdateInboxEntries(IReadOnlyList<InboxEntry> entries, long expectedRevision) => options;
    }
}
