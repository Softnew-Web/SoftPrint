using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Application.Workers;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Infrastructure.Printing;
using SoftPrint.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace SoftPrint.Tests;

public sealed class PrintWorkerAndUpdateTests
{
    [Fact]
    public async Task PrintWorker_MarksUncertain_WhenPrinterOffline()
    {
        var jobs = new MemoryJobRepository();
        var job = jobs.Add(PrintJob.CreatePending("ref-1", "hello"));
        var settings = new MemorySettingsRepository(simulation: false, printer: "HP Offline");
        var catalog = new FakePrinterCatalog([
            new PrinterDeviceInfo("HP Offline", "USB001", "USB", "drv", false, true, false, true, false, "Offline")
        ]);
        var systemSettings = new MemorySystemSettings();
        var router = new PrinterRouter(Options.Create(new SoftPrintFeatureOptions()), systemSettings);
        var strategies = new PrintStrategyResolver([new SimulationPrintStrategy()]);
        var worker = new PrintWorker(
            jobs, settings, systemSettings, router, catalog, strategies,
            new NoOpWebhook(), new NoOpNotifier(), new NoOpTelemetry(),
            Options.Create(new SoftPrintFeatureOptions { PrintJobTimeoutSeconds = 60 }),
            NullLogger<PrintWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var finished = jobs.FindById(job.Id);
        Assert.NotNull(finished);
        Assert.Equal(JobStatus.Uncertain, finished!.Status);
        Assert.Contains("indisponível", finished.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrintWorker_Simulates_WhenSimulationEnabled()
    {
        var jobs = new MemoryJobRepository();
        var job = jobs.Add(PrintJob.CreatePending("ref-sim", "hello"));
        var settings = new MemorySettingsRepository(simulation: true, printer: "Any");
        var catalog = new FakePrinterCatalog([]);
        var systemSettings = new MemorySystemSettings();
        var router = new PrinterRouter(Options.Create(new SoftPrintFeatureOptions()), systemSettings);
        var strategies = new PrintStrategyResolver([new SimulationPrintStrategy()]);
        var worker = new PrintWorker(
            jobs, settings, systemSettings, router, catalog, strategies,
            new NoOpWebhook(), new NoOpNotifier(), new NoOpTelemetry(),
            Options.Create(new SoftPrintFeatureOptions()),
            NullLogger<PrintWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);
        await worker.StopAsync(CancellationToken.None);

        var finished = jobs.FindById(job.Id);
        Assert.NotNull(finished);
        Assert.Equal(JobStatus.Simulated, finished!.Status);
    }

    [Fact]
    public void ResolvePayloadDirectory_FindsNestedExe()
    {
        var root = Path.Combine(Path.GetTempPath(), "softprint-payload-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "x64");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "SoftPrint.exe"), "x");
        try
        {
            Assert.Equal(nested, SoftPrintUpdateApplier.ResolvePayloadDirectory(root));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void LoopbackBindingGuard_RewritesWildcard()
    {
        Assert.True(LoopbackBindingGuard.NeedsRewrite("http://0.0.0.0:5178"));
        Assert.Equal("http://127.0.0.1:5178", LoopbackBindingGuard.RewriteToLoopback("http://0.0.0.0:5178"));
        Assert.False(LoopbackBindingGuard.NeedsRewrite("http://127.0.0.1:5178"));
    }

    [Fact]
    public void PreferredZipAssetName_IsOsAware()
    {
        var name = GitHubReleaseUpdateChecker.PreferredZipAssetName();
        if (OperatingSystem.IsLinux())
            Assert.Equal("softprint-linux-x64.zip", name);
        else
            Assert.StartsWith("SoftPrint-", name);
    }

    private sealed class FakePrinterCatalog(IReadOnlyList<PrinterDeviceInfo> printers) : IPrinterCatalog
    {
        public IReadOnlyList<string> ListInstalled() => printers.Select(p => p.Name).ToArray();
        public IReadOnlyList<PrinterDeviceInfo> ListDetailed() => printers;
        public bool IsInstalled(string printerName) =>
            printers.Any(p => string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class MemorySettingsRepository(bool simulation, string printer) : ISettingsRepository
    {
        public PrintOptions Current { get; private set; } = new()
        {
            Simulation = simulation,
            PrinterName = printer,
            Revision = 1
        };

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
            Current = new PrintOptions
            {
                PrinterName = printerName,
                Simulation = simulation,
                Paused = paused,
                ImageFit = imageFit,
                ImageScalePercent = imageScalePercent,
                PaperSize = paperSize,
                PaperWidthMm = paperWidthMm,
                PaperHeightMm = paperHeightMm,
                PaperLandscape = paperLandscape,
                InboxFolder = inboxFolder,
                InboxEnabled = inboxEnabled,
                DeleteInboxAfterPrint = deleteInboxAfterPrint,
                Revision = expectedRevision + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return Current;
        }
    }

    private sealed class MemorySystemSettings : ISystemSettingsRepository
    {
        public SystemSettings Current { get; private set; } = new(
            100, 30, 60, true, true, "", "", 5000, 15, 8, 30, 400);

        public SystemSettings Update(SystemSettings settings)
        {
            Current = settings;
            return Current;
        }
    }

    private sealed class MemoryJobRepository : IJobRepository
    {
        private readonly List<PrintJob> _jobs = [];
        private readonly object _gate = new();

        public IReadOnlyList<PrintJob> Snapshot()
        {
            lock (_gate) return _jobs.ToArray();
        }

        public PrintJob? PeekNextPending()
        {
            lock (_gate) return _jobs.FirstOrDefault(j => j.Status == JobStatus.Pending);
        }

        public PrintJob? FindByReference(string reference)
        {
            lock (_gate) return _jobs.FirstOrDefault(j =>
                string.Equals(j.Reference, reference, StringComparison.OrdinalIgnoreCase));
        }

        public PrintJob? FindById(Guid id)
        {
            lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id);
        }

        public PrintJob Add(PrintJob job)
        {
            lock (_gate) { _jobs.Add(job); return job; }
        }

        public PrintJob? TakeNextPending(PrintOptions settings) =>
            TakeNextPending(settings.PrinterName, settings.Revision);

        public PrintJob? TakeNextPending(string printerName, long settingsRevision)
        {
            lock (_gate)
            {
                var job = _jobs.FirstOrDefault(j => j.Status == JobStatus.Pending);
                job?.MarkProcessing(printerName, settingsRevision);
                return job;
            }
        }

        public void AppendStep(Guid id, string stage, string where, string message, string? detail = null, bool isError = false)
        {
            lock (_gate) FindById(id)?.AddStep(stage, where, message, detail, isError);
        }

        public void AppendSteps(Guid id, IReadOnlyList<JobStepDraft> steps)
        {
            lock (_gate)
            {
                var job = FindById(id);
                if (job is null) return;
                foreach (var s in steps)
                    job.AddStep(s.Stage, s.Where, s.Message, s.Detail, s.IsError);
            }
        }

        public void Finish(Guid id, JobStatus status, string? error = null, string? errorReason = null, string? errorWhere = null)
        {
            lock (_gate) FindById(id)?.MarkFinished(status, error, errorReason, errorWhere);
        }

        public int PurgeOlderThan(DateTimeOffset cutoff) => 0;
    }

    private sealed class NoOpWebhook : IWebhookNotifier
    {
        public Task NotifyFinishedAsync(PrintJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpNotifier : IAppNotifier
    {
        public void NotifyCompleted(PrintJob job) { }
        public void NotifyUncertain(PrintJob job) { }
        public void NotifyUpdateAvailable(string currentVersion, string latestVersion, bool mandatory) { }
        public void NotifyUpdateFailed(string message) { }
        public void NotifyQueueAlert(string title, string message) { }
    }

    private sealed class NoOpTelemetry : ITelemetryService
    {
        public Task ReportPrintFailureAsync(PrintJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReportHeartbeatAsync(int pending, int processing, string? lastError, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
