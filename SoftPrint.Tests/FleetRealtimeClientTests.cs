using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using SoftPrint.Application.Abstractions;
using SoftPrint.Infrastructure.Integrations;
using Xunit;

namespace SoftPrint.Tests;

public sealed class FleetRealtimeClientTests
{
    [Theory]
    [InlineData("""{"v":1,"type":"softprint.command","room":"softprint:m:1","payload":{"id":5}}""", true)]
    [InlineData("""{"v":1,"type":"softprint.updated"}""", false)]
    [InlineData("""{"event":"ack"}""", false)]
    [InlineData("não é json", false)]
    public void IsCommandSignal(string json, bool expected) =>
        Assert.Equal(expected, FleetRealtimeClient.IsCommandSignal(json));

    [Fact]
    public void Backoff_GrowsAndCaps()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), FleetRealtimeClient.BackoffFor(0, jitter: false));
        Assert.Equal(TimeSpan.FromSeconds(15), FleetRealtimeClient.BackoffFor(2, jitter: false));
        Assert.Equal(TimeSpan.FromSeconds(60), FleetRealtimeClient.BackoffFor(10, jitter: false));
    }

    [Fact]
    public async Task Connects_Subscribes_AndPollsOnCommand()
    {
        string? auth = null;
        string? subscribe = null;
        var sendCommand = new TaskCompletionSource();
        using var server = new TestServer(new WebHostBuilder().Configure(app =>
        {
            app.UseWebSockets();
            app.Run(async ctx =>
            {
                auth = ctx.Request.Headers.Authorization.ToString();
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var buf = new byte[1024];
                var r = await ws.ReceiveAsync(buf, CancellationToken.None);
                subscribe = Encoding.UTF8.GetString(buf, 0, r.Count);
                await sendCommand.Task;
                var msg = Encoding.UTF8.GetBytes("""{"v":1,"type":"softprint.command","payload":{"id":1}}""");
                await ws.SendAsync(msg, WebSocketMessageType.Text, true, CancellationToken.None);
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ContinueWith(_ => { });
            });
        }));

        var fleet = new RealtimeFleet();
        var poller = new CountingPoller();
        var client = new FleetRealtimeClient(fleet, poller, NullLogger<FleetRealtimeClient>.Instance)
        {
            StartDelay = TimeSpan.Zero,
            Connect = async (uri, token, ct) =>
            {
                var wsc = server!.CreateWebSocketClient();
                wsc.ConfigureRequest = req => req.Headers.Authorization = "Bearer " + token;
                return await wsc.ConnectAsync(uri, ct);
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.StartAsync(cts.Token);
        await poller.WaitForAsync(1, cts.Token);          // busca ao conectar
        Assert.True(fleet.Connected);
        sendCommand.SetResult();
        await poller.WaitForAsync(2, cts.Token);          // busca pelo aviso
        await client.StopAsync(CancellationToken.None);

        Assert.Equal("Bearer jwt-teste", auth);
        using var doc = JsonDocument.Parse(subscribe!);
        Assert.Equal("subscribe", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("softprint:m:3", doc.RootElement.GetProperty("rooms")[0].GetString());
        Assert.False(fleet.Connected);
    }

    // ---- Reconexão, espera e renovação ----

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }

    private static async Task HoldOpen(WebSocket ws, CancellationToken ct)
    {
        try
        {
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { /* cliente saiu */ }
    }

    /// <summary>Servidor que aceita, lê o subscribe e entrega o socket ao comportamento do teste.</summary>
    private static TestServer NewServer(Func<WebSocket, CancellationToken, Task> behavior) =>
        new(new WebHostBuilder().Configure(app =>
        {
            app.UseWebSockets();
            app.Run(async ctx =>
            {
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var buf = new byte[1024];
                await ws.ReceiveAsync(buf, ctx.RequestAborted);
                await behavior(ws, ctx.RequestAborted);
            });
        }));

    private sealed class Rig
    {
        public readonly RealtimeFleet Fleet = new();
        public readonly CountingPoller Poller = new();
        public readonly List<TimeSpan> Delays = new();
        public readonly List<bool> ConnectedAtDelay = new();
        public int Connects;
        public FleetRealtimeClient Client = null!;

        public static Rig Create(TestServer? server, TimeSpan? renewBefore = null, TimeSpan? minRenew = null,
            TimeSpan? renewFallback = null, Func<WebSocket>? fakeSocket = null)
        {
            var rig = new Rig();
            rig.Client = new FleetRealtimeClient(rig.Fleet, rig.Poller, NullLogger<FleetRealtimeClient>.Instance)
            {
                StartDelay = TimeSpan.Zero,
                RenewBefore = renewBefore ?? TimeSpan.FromMinutes(30),
                MinRenew = minRenew ?? TimeSpan.FromMinutes(1),
                RenewFallback = renewFallback ?? TimeSpan.FromMinutes(30),
                Delay = async (d, ct) =>
                {
                    lock (rig.Delays)
                    {
                        rig.Delays.Add(d);
                        rig.ConnectedAtDelay.Add(rig.Fleet.Connected);
                    }
                    await Task.Delay(5, ct);
                },
                Connect = async (uri, token, ct) =>
                {
                    Interlocked.Increment(ref rig.Connects);
                    if (fakeSocket is not null) return fakeSocket();
                    var wsc = server!.CreateWebSocketClient();
                    return await wsc.ConnectAsync(uri, ct);
                }
            };
            return rig;
        }

        public int DelayCount { get { lock (Delays) return Delays.Count; } }
        public TimeSpan[] DelaySnapshot() { lock (Delays) return Delays.ToArray(); }
        public bool[] ConnectedSnapshot() { lock (Delays) return ConnectedAtDelay.ToArray(); }
    }

    [Fact]
    public async Task ServerClose_WaitsBeforeReconnecting()
    {
        using var server = NewServer(async (ws, ct) =>
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct));
        var rig = Rig.Create(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => Volatile.Read(ref rig.Connects) >= 3, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        var delays = rig.DelaySnapshot();
        Assert.True(delays.Length >= 2, "esperava espera entre as conexões");
        Assert.True(delays.Length >= Volatile.Read(ref rig.Connects) - 1, "toda reconexão precisa passar por espera");
        Assert.All(delays, d => Assert.True(d > TimeSpan.Zero));
        Assert.InRange(delays[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));     // BackoffFor(0) +-50%
        Assert.InRange(delays[1], TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(7.5)); // BackoffFor(1) +-50%
    }

    [Fact]
    public async Task SystemError_DropsConnection_AndCountsAsOpenFailure()
    {
        using var server = NewServer(async (ws, ct) =>
        {
            var msg = Encoding.UTF8.GetBytes("""{"type":"system.error","message":"sala negada"}""");
            await ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
            await HoldOpen(ws, ct);
        });
        var rig = Rig.Create(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => rig.DelayCount >= 3, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        var delays = rig.DelaySnapshot();
        Assert.InRange(delays[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromMinutes(5), delays[2]);   // 3 falhas de abertura seguidas
        Assert.All(rig.ConnectedSnapshot(), c => Assert.False(c));
        Assert.False(rig.Fleet.Connected);
    }

    [Fact]
    public async Task NullTicket_ThreeTimes_WaitsFiveMinutes()
    {
        using var server = NewServer((_, _) => Task.CompletedTask);
        var rig = Rig.Create(server);
        rig.Fleet.NextTicket = () => null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => rig.DelayCount >= 3, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        var delays = rig.DelaySnapshot();
        Assert.True(delays[0] < TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromMinutes(5), delays[2]);
        Assert.Equal(0, rig.Connects);
    }

    [Fact]
    public async Task TokenChanged_ReconnectsWithoutWaiting_AndAsksNewTicket()
    {
        using var server = NewServer(HoldOpen);
        var rig = Rig.Create(server);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => rig.Fleet.Connected, cts.Token);
            Assert.Equal(1, rig.Fleet.TicketCalls);
            rig.Fleet.FireTokenChanged();
            await WaitUntil(() => rig.Fleet.TicketCalls >= 2 && Volatile.Read(ref rig.Connects) >= 2, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, rig.DelayCount);
    }

    [Fact]
    public async Task NearExpiry_ReconnectsToRenew()
    {
        using var server = NewServer(HoldOpen);
        var rig = Rig.Create(server, renewBefore: TimeSpan.Zero, minRenew: TimeSpan.FromMilliseconds(50));
        rig.Fleet.NextTicket = () => new FleetRealtimeTicket(
            new Uri("ws://localhost/v1/ws"), "jwt-teste", "softprint:m:3", DateTimeOffset.UtcNow.AddMilliseconds(300));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => Volatile.Read(ref rig.Connects) >= 2, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, rig.DelayCount);
        Assert.True(rig.Fleet.TicketCalls >= 2);
    }

    [Fact]
    public async Task NearExpiry_BelowMinRenew_UsesFallbackInterval()
    {
        using var server = NewServer(HoldOpen);
        var rig = Rig.Create(server, renewBefore: TimeSpan.Zero, renewFallback: TimeSpan.FromMilliseconds(100));
        rig.Fleet.NextTicket = () => new FleetRealtimeTicket(
            new Uri("ws://localhost/v1/ws"), "jwt-teste", "softprint:m:3", DateTimeOffset.UtcNow.AddSeconds(-1));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => Volatile.Read(ref rig.Connects) >= 2, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, rig.DelayCount);
    }

    [Fact]
    public async Task CancellationWithoutRequest_IsTreatedAsDrop_AndWaits()
    {
        var rig = Rig.Create(null, fakeSocket: () => new ThrowingSocket());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => rig.DelayCount >= 1, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        var delays = rig.DelaySnapshot();
        Assert.InRange(delays[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        Assert.False(rig.Fleet.Connected);
    }

    [Fact]
    public async Task ManySignals_DuringSlowPoll_CoalesceIntoOneMorePoll()
    {
        var gate = new TaskCompletionSource();
        var signalsSent = new TaskCompletionSource();
        using var server = NewServer(async (ws, ct) =>
        {
            var msg = Encoding.UTF8.GetBytes("""{"v":1,"type":"softprint.command","payload":{"id":1}}""");
            await Task.Delay(100, ct); // deixa a primeira busca (ao conectar) começar e travar
            for (var i = 0; i < 5; i++) await ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
            signalsSent.SetResult();
            await HoldOpen(ws, ct);
        });
        var rig = Rig.Create(server);
        rig.Poller.FirstPollGate = gate.Task;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Client.StartAsync(cts.Token);
        try
        {
            await rig.Poller.WaitForAsync(1, cts.Token);
            await signalsSent.Task.WaitAsync(cts.Token);
            await Task.Delay(300, cts.Token);          // o cliente consome os 5 avisos
            Assert.Equal(1, rig.Poller.Count);          // busca lenta ainda em andamento
            gate.SetResult();
            await rig.Poller.WaitForAsync(2, cts.Token);
            await Task.Delay(300, cts.Token);
        }
        finally
        {
            await rig.Client.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, rig.Poller.Count);
    }

    /// <summary>Socket "aberto" cujo receive lança OperationCanceledException sem nenhum cancelamento pedido.</summary>
    private sealed class ThrowingSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => WebSocketState.Open;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new OperationCanceledException("abortado");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingPoller : IFleetCommandPoller
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        /// <summary>Quando definido, a primeira busca fica pendurada até ele completar (poller lento).</summary>
        public Task? FirstPollGate { get; set; }

        public async Task<int> PollAsync(CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _count);
            if (n == 1 && FirstPollGate is { } gate) await gate.WaitAsync(cancellationToken);
            return 0;
        }

        public async Task WaitForAsync(int count, CancellationToken ct)
        {
            while (Volatile.Read(ref _count) < count) await Task.Delay(20, ct);
        }
    }

    private sealed class RealtimeFleet : IFleetService
    {
        private CancellationTokenSource _tokenCts = new();
        private int _ticketCalls;

        public volatile bool Connected;
        public int TicketCalls => Volatile.Read(ref _ticketCalls);
        public Func<FleetRealtimeTicket?> NextTicket { get; set; } = () => new FleetRealtimeTicket(
            new Uri("ws://localhost/v1/ws"), "jwt-teste", "softprint:m:3", DateTimeOffset.UtcNow.AddHours(12));

        /// <summary>Simula a troca de token: cancela o token atual e passa a entregar um novo.</summary>
        public void FireTokenChanged()
        {
            var old = _tokenCts;
            _tokenCts = new CancellationTokenSource();
            old.Cancel();
        }

        public FleetStatus GetStatus() => new(true, false, "", "m1", "pc", "https://b.test", 40, null, DateTimeOffset.Now, true, null, null);
        public Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _ticketCalls);
            return Task.FromResult(NextTicket());
        }
        public void SetRealtimeConnected(bool connected) => Connected = connected;
        public CancellationToken TokenChanged => _tokenCts.Token;

        public void SetToken(string? token) { }
        public Task ChangeTokenAsync(string? token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default) => Task.FromResult(new FleetSendResult(true, ""));
        public Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default) => Task.FromResult(new FleetSendResult(true, ""));
        public Task<IReadOnlyList<FleetCommand>> GetCommandsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FleetCommand>>([]);
        public Task<bool> SendCommandResultAsync(long commandId, bool ok, string message, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void NoteCommand(string description) { }
        public void RememberPendingUpdate(long commandId) { }
        public FleetPendingUpdate? GetPendingUpdate() => null;
        public void ClearPendingUpdate() { }
    }
}
