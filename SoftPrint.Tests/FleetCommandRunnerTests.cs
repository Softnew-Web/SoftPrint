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
    private readonly FakeSupport _support;

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
        _runner = new FleetCommandRunner(_fleet, _updates, _settings, _jobs, _support = new FakeSupport(_root),
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

    [Fact]
    public async Task ReprintFailed_Twice_DoesNotReprintAgain()
    {
        var failed = _jobs.Submit("ref-falhou", "texto");
        _jobsRepo.TakePending(failed.Id, "imp", 1);
        _jobsRepo.Finish(failed.Id, JobStatus.Uncertain, "papel acabou");

        _fleet.Queue(new FleetCommand(20, "reprint_failed", null));
        await _runner.PollAsync();
        _fleet.Queue(new FleetCommand(21, "reprint_failed", null));
        await _runner.PollAsync();

        Assert.Equal(2, _jobs.List().Count);
        Assert.Equal((21L, true, "0 trabalho(s) reenviado(s)."), _fleet.Results[1]);
    }

    [Fact]
    public async Task ReprintFailed_IgnoresOlderThan24Hours()
    {
        _jobsRepo.Add(new PrintJob(Guid.NewGuid(), "velho", "texto", JobStatus.Uncertain,
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2)));
        _fleet.Queue(new FleetCommand(22, "reprint_failed", null));

        await _runner.PollAsync();

        Assert.Equal((22L, true, "0 trabalho(s) reenviado(s)."), _fleet.Results[0]);
        Assert.Single(_jobs.List());
    }

    [Fact]
    public async Task Update_WhenRememberThrows_FailsWithoutStarting()
    {
        _fleet.ThrowOnRemember = true;
        _fleet.Queue(new FleetCommand(23, "update", null));

        await _runner.PollAsync();

        Assert.False(_updates.Started);
        Assert.False(_updates.TryStartCalled);
        Assert.False(_fleet.Results[0].Ok);
        Assert.StartsWith("Não foi possível guardar", _fleet.Results[0].Message);
    }

    [Fact]
    public async Task PendingUpdate_WhenClearThrows_DoesNotPropagate()
    {
        _fleet.PendingUpdate = new FleetPendingUpdate(24, "0.0.1");
        _fleet.ThrowOnClear = true;

        await _runner.ReportPendingUpdateAsync();

        Assert.Single(_fleet.Results);
    }

    [Fact]
    public async Task Update_WithExistingPending_Refuses_AndKeepsPending()
    {
        _updates.InProgress = true;
        _fleet.PendingUpdate = new FleetPendingUpdate(30, "0.0.1");
        _fleet.Queue(new FleetCommand(25, "update", null));

        await _runner.PollAsync();

        Assert.False(_updates.TryStartCalled);
        Assert.Equal((25L, false, "Já existe uma atualização em andamento."), _fleet.Results[0]);
        Assert.Equal(30, _fleet.PendingUpdate!.CommandId);
    }

    [Fact]
    public async Task Update_WithStalePending_ReportsOldFailure_AndStartsNew()
    {
        _updates.ErrorText = "Nenhuma atualização disponível.";
        _fleet.PendingUpdate = new FleetPendingUpdate(40, "0.0.1");
        _fleet.Queue(new FleetCommand(41, "update", null));

        await _runner.PollAsync();

        Assert.Equal((40L, false, "A atualização não foi concluída. Nenhuma atualização disponível."), _fleet.Results[0]);
        Assert.True(_updates.TryStartCalled);
        Assert.Equal(41, _fleet.PendingUpdate!.CommandId);
        Assert.Single(_fleet.Results);
    }

    [Fact]
    public async Task Update_StopsBatch_RemainingGetRestartFailure()
    {
        _fleet.Queue(new FleetCommand(26, "update", null), new FleetCommand(27, "refresh", null));

        await _runner.PollAsync();

        Assert.Equal(0, _fleet.Heartbeats);
        Assert.Equal(new[] { (27L, false, "O SoftPrint vai reiniciar para atualizar; envie o comando de novo depois.") },
            _fleet.Results);
    }

    [Fact]
    public async Task ThrowingCommand_ReportsClippedMessage()
    {
        _support.ThrowMessage = new string('x', 600);
        _fleet.Queue(new FleetCommand(28, "support_package", null));

        await _runner.PollAsync();

        Assert.False(_fleet.Results[0].Ok);
        Assert.Equal(500, _fleet.Results[0].Message.Length);
    }

    [Fact]
    public async Task SupportPackage_DeletesZipAfterUpload()
    {
        _fleet.Queue(new FleetCommand(31, "support_package", null));

        await _runner.PollAsync();

        Assert.False(File.Exists(Path.Combine(_root, "pacote.zip")));
    }

    [Fact]
    public async Task ResultThatFailsOnce_IsRetried()
    {
        _fleet.FailResultSends = 1;
        _fleet.Queue(new FleetCommand(29, "pause", null));

        await _runner.PollAsync();

        Assert.Equal(2, _fleet.ResultCalls);
        Assert.Single(_fleet.Results);
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
        public string? ThrowMessage { get; set; }

        public string CreateBundle()
        {
            if (ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
            var path = Path.Combine(root, "pacote.zip");
            File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04]);
            return path;
        }
    }

    private sealed class FakeUpdates : IUpdateApplier
    {
        public bool Started { get; private set; }
        public bool TryStartCalled { get; private set; }
        public string? Refuse { get; set; }
        public bool InProgress { get; set; }
        public string? ErrorText { get; set; }
        public UpdateApplyStatus Status => GetStatus();

        public bool TryStart(out string? error)
        {
            TryStartCalled = true;
            error = Refuse;
            Started = Refuse is null;
            return Started;
        }

        public bool TryStartRollback(string? targetVersion, out string? error) => throw new NotSupportedException();
        public UpdateApplyStatus GetStatus() => new("idle", 0, "", InProgress, ErrorText is not null, false, ErrorText);
    }

    internal sealed class FakeFleet : IFleetService
    {
        private readonly Queue<FleetCommand> _queue = new();
        public List<(long Id, bool Ok, string Message)> Results { get; } = [];
        public FleetPendingUpdate? PendingUpdate { get; set; }
        public int Heartbeats { get; private set; }
        public string? UploadedNote { get; private set; }
        public bool ThrowOnRemember { get; set; }
        public bool ThrowOnClear { get; set; }
        public int FailResultSends { get; set; }
        public int ResultCalls { get; private set; }

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
            ResultCalls++;
            if (FailResultSends > 0)
            {
                FailResultSends--;
                return Task.FromResult(false);
            }
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

        public void RememberPendingUpdate(long commandId)
        {
            if (ThrowOnRemember) throw new IOException("disco cheio");
            PendingUpdate = new FleetPendingUpdate(commandId, SoftPrintVersion.Current);
        }
        public FleetPendingUpdate? GetPendingUpdate() => PendingUpdate;
        public void ClearPendingUpdate()
        {
            if (ThrowOnClear) throw new IOException("disco cheio");
            PendingUpdate = null;
        }

        public FleetStatus GetStatus() => new(true, false, "", "m1", "pc", "https://b.test", 40, null, null, true, null, null);
        public void SetToken(string? token) { }
        public Task ChangeTokenAsync(string? token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public CancellationToken TokenChanged => CancellationToken.None;
        public Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default) => Task.FromResult<FleetRealtimeTicket?>(null);
        public void SetRealtimeConnected(bool connected) { }
        public void NoteCommand(string description) { }
    }
}
