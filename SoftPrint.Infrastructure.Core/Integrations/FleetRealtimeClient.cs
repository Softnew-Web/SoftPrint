using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Integrations;

/// <summary>
/// WebSocket com o hub da Softnew: só recebe avisos de "comando novo" na sala desta máquina.
/// O comando em si é buscado por HTTPS (<see cref="IFleetCommandPoller"/>). Sem conexão, o heartbeat
/// cobre a busca a cada 15 s.
/// </summary>
public sealed class FleetRealtimeClient(
    IFleetService fleet,
    IFleetCommandPoller poller,
    ILogger<FleetRealtimeClient> log) : BackgroundService
{
    private static readonly int[] BackoffSeconds = [2, 5, 15, 30, 60];
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(30);

    internal TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Abre a conexão (trocável nos testes).</summary>
    internal Func<Uri, string, CancellationToken, Task<WebSocket>> Connect { get; init; } = DefaultConnectAsync;

    internal static bool IsCommandSignal(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("type", out var t)
                   && t.ValueKind == JsonValueKind.String
                   && t.GetString() == "softprint.command";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static TimeSpan BackoffFor(int failures, bool jitter = true)
    {
        var seconds = BackoffSeconds[Math.Min(failures, BackoffSeconds.Length - 1)];
#if NET6_0_OR_GREATER
        var r = Random.Shared.NextDouble();
#else
        var r = new Random().NextDouble();
#endif
        var extra = jitter ? r * seconds * 0.2 : 0;
        return TimeSpan.FromSeconds(seconds + extra);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = fleet.GetStatus();
            // Só depois do primeiro heartbeat com sucesso a máquina existe no backend.
            if (!status.Configured || status.LastSuccessAt is null)
            {
                if (!await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken)) return;
                continue;
            }

            var tokenChanged = fleet.TokenChanged;
            try
            {
                var ticket = await fleet.GetRealtimeTicketAsync(stoppingToken).ConfigureAwait(false);
                if (ticket is null)
                {
                    // Servidor sem tempo real, ou recusou: tenta de novo bem mais tarde.
                    failures++;
                    var wait = failures >= 3 ? TimeSpan.FromMinutes(5) : BackoffFor(failures);
                    if (!await DelayAsync(wait, stoppingToken)) return;
                    continue;
                }

                await RunConnectionAsync(ticket, tokenChanged, stoppingToken).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                log.LogDebug(ex, "Tempo real do monitoramento caiu");
            }
            finally
            {
                fleet.SetRealtimeConnected(false);
            }

            if (failures > 0 && !await DelayAsync(BackoffFor(failures - 1), stoppingToken)) return;
        }
    }

    private async Task RunConnectionAsync(FleetRealtimeTicket ticket, CancellationToken tokenChanged, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, tokenChanged);
        var renewIn = ticket.ExpiresAt - DateTimeOffset.UtcNow - RenewBefore;
        if (renewIn > TimeSpan.Zero) linked.CancelAfter(renewIn);
        var ct = linked.Token;

        using var ws = await Connect(ticket.WsUrl, ticket.Token, ct).ConfigureAwait(false);
        var subscribe = JsonSerializer.Serialize(new { action = "subscribe", rooms = new[] { ticket.Room } });
        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(subscribe)), WebSocketMessageType.Text, true, ct)
            .ConfigureAwait(false);

        fleet.SetRealtimeConnected(true);
        log.LogInformation("Tempo real do monitoramento conectado");
        _ = PollSafeAsync(stoppingToken); // pega o que chegou enquanto estava desconectado

        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (message.Length <= 64 * 1024 && IsCommandSignal(Encoding.UTF8.GetString(message.ToArray())))
                    _ = PollSafeAsync(stoppingToken);
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Renovação do token ou token trocado: reconecta na próxima volta.
        }

        if (ws.State == WebSocketState.Open)
        {
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
            catch { /* já caiu */ }
        }
    }

    private async Task PollSafeAsync(CancellationToken ct)
    {
        try { await poller.PollAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogDebug(ex, "Busca de comandos falhou"); }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<WebSocket> DefaultConnectAsync(Uri uri, string token, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }
}
