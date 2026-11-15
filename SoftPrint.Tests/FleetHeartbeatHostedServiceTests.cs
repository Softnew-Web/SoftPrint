using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.Infrastructure.Operations;
using SoftPrint.Infrastructure.Persistence;
using Xunit;

namespace SoftPrint.Tests;

public sealed class FleetHeartbeatHostedServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softprint-hb-" + Guid.NewGuid().ToString("N"));

    public FleetHeartbeatHostedServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task Heartbeat_KeepsGoing_WhileACommandIsRunning()
    {
        var fleet = new FleetCommandRunnerTests.FakeFleet { PendingCommandsInHeartbeat = 1 };
        var poller = new StuckPoller();
        var jobs = new JobQueueService(new JsonJobRepository(new Paths(_root)), new PrintJobFactory(),
            new TemplateRenderer(Options.Create(new SoftPrintFeatureOptions())));

        var service = new FleetHeartbeatHostedService(fleet, poller, new NoReporter(), jobs, new Settings(),
            Options.Create(new SoftPrintFeatureOptions()), NullLogger<FleetHeartbeatHostedService>.Instance)
        {
            Tick = TimeSpan.FromMilliseconds(20),
            StartDelay = TimeSpan.Zero,
            IntervalOverride = TimeSpan.FromMilliseconds(40),
        };

        await service.StartAsync(CancellationToken.None);
        try
        {
            await poller.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = fleet.Heartbeats;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (fleet.Heartbeats < before + 2 && DateTime.UtcNow < deadline) await Task.Delay(20);

            Assert.True(fleet.Heartbeats >= before + 2, "o heartbeat parou enquanto o comando rodava");
            Assert.Equal(1, poller.Calls); // a busca presa não é disparada de novo
        }
        finally
        {
            poller.Release.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class StuckPoller : IFleetCommandPoller
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<int> PollAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await Release.Task;
            return 0;
        }
    }

    private sealed class NoReporter : IPendingUpdateReporter
    {
        public Task ReportPendingUpdateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record Paths(string Root) : IAppPaths
    {
        public string DataRoot => Path.Combine(Root, "data");
        public string ConfigRoot => Path.Combine(Root, "config");
    }

    private sealed class Settings : ISettingsRepository
    {
        public PrintOptions Current { get; } = new();

        public PrintOptions Update(string printerName, bool simulation, bool paused, ImageFitMode imageFit,
            int imageScalePercent, PaperSizeKind paperSize, double paperWidthMm, double paperHeightMm,
            bool paperLandscape, long expectedRevision) => throw new NotSupportedException();

        public PrintOptions UpdateInboxEntries(IReadOnlyList<InboxEntry> entries, long expectedRevision) =>
            throw new NotSupportedException();
    }
}
