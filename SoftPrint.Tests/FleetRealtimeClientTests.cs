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
                var wsc = server.CreateWebSocketClient();
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

    private sealed class CountingPoller : IFleetCommandPoller
    {
        private int _count;
        public Task<int> PollAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(0);
        }

        public async Task WaitForAsync(int count, CancellationToken ct)
        {
            while (Volatile.Read(ref _count) < count) await Task.Delay(20, ct);
        }
    }

    private sealed class RealtimeFleet : IFleetService
    {
        public bool Connected { get; private set; }
        public FleetStatus GetStatus() => new(true, false, "", "m1", "pc", "https://b.test", 40, null, DateTimeOffset.Now, true, null, null);
        public Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<FleetRealtimeTicket?>(new FleetRealtimeTicket(
                new Uri("ws://localhost/v1/ws"), "jwt-teste", "softprint:m:3", DateTimeOffset.UtcNow.AddHours(12)));
        public void SetRealtimeConnected(bool connected) => Connected = connected;
        public CancellationToken TokenChanged => CancellationToken.None;

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
