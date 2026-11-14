using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.Infrastructure.Persistence;
using Xunit;

namespace SoftPrint.Tests;

public sealed class FleetCommandRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softprint-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFleet _fleet = new();
    private readonly FakeUpdates _updates = new();
    private readonly JsonSettingsRepository _settingsRepo;
    private readonly SettingsService _settings;
    private readonly JsonJobRepository _jobsRepo;
    private readonly JobQueueService _jobs;
    private readonly FleetCommandRunner _runner;

    public FleetCommandRunnerTests()
    {
        Directory.CreateDirectory(_root);
        var paths = new TestPaths(_root);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        _settingsRepo = new JsonSettingsRepository(paths, config);
        _settings = new SettingsService(_settingsRepo, new NoPrinters());
        _jobsRepo = new JsonJobRepository(paths);
        _jobs = new JobQueueService(_jobsRepo, new PrintJobFactory(),
            new TemplateRenderer(Options.Create(new SoftPrintFeatureOptions())));
        _runner = new FleetCommandRunner(_fleet, _updates, _settings, _jobs, new FakeSupport(_root),
            NullLogger<FleetCommandRunner>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task Pause_ThenResume_ChangesSettings_AndReportsDone()
    {
        _fleet.Queue(new FleetCommand(1, "pause", "ana"), new FleetCommand(2, "resume", "ana"));

        await _runner.PollAsync();

        Assert.False(_settings.Current.Paused);
        Assert.Equal(new[] { (1L, true, "Fila pausada."), (2L, true, "Fila retomada.") }, _fleet.Results);
    }

    [Fact]
    public async Task UnknownType_ReportsFailure()
    {
        _fleet.Queue(new FleetCommand(3, "format_c", null));

        await _runner.PollAsync();

        Assert.Equal(new[] { (3L, false, "Comando desconhecido: format_c") }, _fleet.Results);
    }

    [Fact]
    public async Task SameIdTwice_RunsOnce()
    {
        _fleet.Queue(new FleetCommand(4, "refresh", null));
        await _runner.PollAsync();
        _fleet.Queue(new FleetCommand(4, "refresh", null));
        await _runner.PollAsync();

        Assert.Single(_fleet.Results);
        Assert.Equal(1, _fleet.Heartbeats);
    }

    [Fact]
    public async Task TestPrint_SubmitsGuidedJob()
    {
        _fleet.Queue(new FleetCommand(5, "test_print", "ana"));

        await _runner.PollAsync();

        var job = Assert.Single(_jobs.List());
        Assert.Contains("Pedido por: ana", job.Text);
        Assert.True(_fleet.Results[0].Ok);
    }

    [Fact]
    public async Task ReprintFailed_ReprintsOnlyRecentUncertain()
    {
        var failed = _jobs.Submit("ref-falhou", "texto");
        _jobsRepo.TakePending(failed.Id, "imp", 1);
        _jobsRepo.Finish(failed.Id, JobStatus.Uncertain, "papel acabou");
        _fleet.Queue(new FleetCommand(6, "reprint_failed", null));

        await _runner.PollAsync();

        Assert.Equal((6L, true, "1 trabalho(s) reenviado(s)."), _fleet.Results[0]);
        Assert.Equal(2, _jobs.List().Count);
    }

    [Fact]
    public async Task Update_StartsApplier_AndWaitsForRestart()
    {
        _fleet.Queue(new FleetCommand(7, "update", null));

        await _runner.PollAsync();

        Assert.True(_updates.Started);
        Assert.Empty(_fleet.Results);
        Assert.Equal(7, _fleet.PendingUpdate!.CommandId);
    }

    [Fact]
    public async Task Update_WhenApplierRefuses_ReportsFailure()
    {
        _updates.Refuse = "Já está na versão mais nova.";
        _fleet.Queue(new FleetCommand(8, "update", null));

        await _runner.PollAsync();

        Assert.Equal(new[] { (8L, false, "Já está na versão mais nova.") }, _fleet.Results);
        Assert.Null(_fleet.PendingUpdate);
    }

    [Fact]
    public async Task PendingUpdate_AfterRestart_ReportsNewVersion()
    {
        _fleet.PendingUpdate = new FleetPendingUpdate(9, "0.0.1");

        await _runner.ReportPendingUpdateAsync();

        Assert.Equal((9L, true, $"Atualizado de 0.0.1 para {SoftPrintVersion.Current}."), _fleet.Results[0]);
        Assert.Null(_fleet.PendingUpdate);
    }

    [Fact]
    public async Task PendingUpdate_SameVersion_ReportsFailure()
    {
        _fleet.PendingUpdate = new FleetPendingUpdate(10, SoftPrintVersion.Current);

        await _runner.ReportPendingUpdateAsync();

        Assert.False(_fleet.Results[0].Ok);
        Assert.StartsWith("A atualização não foi concluída", _fleet.Results[0].Message);
    }

    [Fact]
    public async Task SupportPackage_IsUploaded()
    {
        _fleet.Queue(new FleetCommand(11, "support_package", "ana"));

        await _runner.PollAsync();

        Assert.Equal("Pedido pelo painel por ana", _fleet.UploadedNote);
        Assert.True(_fleet.Results[0].Ok);
    }

    private sealed record TestPaths(string Root) : IAppPaths
    {
        public string DataRoot => Path.Combine(Root, "data");
        public string ConfigRoot => Path.Combine(Root, "config");
    }

    private sealed class NoPrinters : IPrinterCatalog
    {
        public IReadOnlyList<string> ListInstalled() => [];
        public IReadOnlyList<PrinterDeviceInfo> ListDetailed() => [];
        public bool IsInstalled(string printerName) => true;
    }

    private sealed class FakeSupport(string root) : ISupportBundleService
    {
        public string CreateBundle()
        {
            var path = Path.Combine(root, "pacote.zip");
            File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04]);
            return path;
        }
    }

    private sealed class FakeUpdates : IUpdateApplier
    {
        public bool Started { get; private set; }
        public string? Refuse { get; set; }
        public UpdateApplyStatus Status => GetStatus();

        public bool TryStart(out string? error)
        {
            error = Refuse;
            Started = Refuse is null;
            return Started;
        }

        public bool TryStartRollback(string? targetVersion, out string? error) => throw new NotSupportedException();
        public UpdateApplyStatus GetStatus() => new("idle", 0, "", false, false, false);
    }

    internal sealed class FakeFleet : IFleetService
    {
        private readonly Queue<FleetCommand> _queue = new();
        public List<(long Id, bool Ok, string Message)> Results { get; } = [];
        public FleetPendingUpdate? PendingUpdate { get; set; }
        public int Heartbeats { get; private set; }
        public string? UploadedNote { get; private set; }

        public void Queue(params FleetCommand[] commands)
        {
            foreach (var c in commands) _queue.Enqueue(c);
        }

        public Task<IReadOnlyList<FleetCommand>> GetCommandsAsync(CancellationToken cancellationToken = default)
        {
            var list = _queue.ToList();
            _queue.Clear();
            return Task.FromResult<IReadOnlyList<FleetCommand>>(list);
        }

        public Task<bool> SendCommandResultAsync(long commandId, bool ok, string message, CancellationToken cancellationToken = default)
        {
            Results.Add((commandId, ok, message));
            return Task.FromResult(true);
        }

        public Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default)
        {
            Heartbeats++;
            return Task.FromResult(new FleetSendResult(true, "ok"));
        }

        public Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default)
        {
            UploadedNote = note;
            return Task.FromResult(new FleetSendResult(true, "Pacote enviado ao suporte Softnew."));
        }

        public void RememberPendingUpdate(long commandId) => PendingUpdate = new FleetPendingUpdate(commandId, SoftPrintVersion.Current);
        public FleetPendingUpdate? GetPendingUpdate() => PendingUpdate;
        public void ClearPendingUpdate() => PendingUpdate = null;

        public FleetStatus GetStatus() => new(true, false, "", "m1", "pc", "https://b.test", 40, null, null, true, null, null);
        public void SetToken(string? token) { }
        public Task ChangeTokenAsync(string? token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public CancellationToken TokenChanged => CancellationToken.None;
        public Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default) => Task.FromResult<FleetRealtimeTicket?>(null);
        public void SetRealtimeConnected(bool connected) { }
        public void NoteCommand(string description) { }
    }
}
