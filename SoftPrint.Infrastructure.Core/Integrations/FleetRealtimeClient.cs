using System.Diagnostics;
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
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRenew = TimeSpan.FromHours(12);

    private int _pollDirty;
    private int _pollRunning;
    private int _signals;

    /// <summary>Avisos de comando recebidos (para os testes esperarem por condição).</summary>
    internal int CommandSignals => Volatile.Read(ref _signals);

    internal TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Quanto antes de expirar o ticket a conexão é renovada.</summary>
    internal TimeSpan RenewBefore { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Menor intervalo até a renovação (evita renovar em loop com ticket quase expirado).</summary>
    internal TimeSpan MinRenew { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Intervalo de renovação quando o ticket já está perto de expirar (abaixo de <see cref="MinRenew"/>).</summary>
    internal TimeSpan RenewFallback { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Espera entre tentativas (trocável nos testes).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (d, ct) => Task.Delay(d, ct);

    /// <summary>Abre a conexão (trocável nos testes).</summary>
    internal Func<Uri, string, CancellationToken, Task<WebSocket>> Connect { get; init; } = DefaultConnectAsync;

    private enum EndReason { Renew, TokenChanged, ServerClosed, Dropped, Rejected, Stopped }

    private sealed record Outcome(EndReason Reason, WebSocketCloseStatus? CloseStatus, TimeSpan Duration);

    internal static bool IsCommandSignal(string json) => TypeOf(json) == "softprint.command";

    internal static bool IsSystemError(string json, out string message)
    {
        message = "";
        if (TypeOf(json) != "system.error") return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in new[] { "message", "error", "code" })
            {
                if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    message = p.GetString() ?? "";
                    break;
                }
            }
        }
        catch (JsonException) { }
        return true;
    }

    private static string? TypeOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("type", out var t)
                   && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static TimeSpan BackoffFor(int failures, bool jitter = true)
    {
        var seconds = BackoffSeconds[Math.Max(0, Math.Min(failures, BackoffSeconds.Length - 1))];
        if (!jitter) return TimeSpan.FromSeconds(seconds);
#if NET6_0_OR_GREATER
        var r = Random.Shared.NextDouble();
#else
        var r = new Random().NextDouble();
#endif
        // ±50%, no mínimo 1 s.
        return TimeSpan.FromSeconds(Math.Max(1, seconds * (0.5 + r)));
    }

    /// <summary>Quando renovar a conexão: antes de expirar; abaixo de <paramref name="minRenew"/> usa o fallback; teto de 12 h.</summary>
    internal static TimeSpan RenewDelay(DateTimeOffset expiresAt, DateTimeOffset now, TimeSpan renewBefore, TimeSpan minRenew, TimeSpan fallback)
    {
        var renewIn = expiresAt - now - renewBefore;
        if (renewIn < minRenew) renewIn = fallback; // hub só valida o JWT no handshake
        return renewIn > MaxRenew ? MaxRenew : renewIn;
    }

    private static TimeSpan OpenWait(int openFailures) =>
        openFailures >= 3 ? TimeSpan.FromMinutes(5) : BackoffFor(openFailures - 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var openFailures = 0;
        var dropFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = fleet.GetStatus();
            // Só depois do primeiro heartbeat com sucesso a máquina existe no backend.
            if (!status.Configured || status.LastSuccessAt is null)
            {
                if (!await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken)) return;
                continue;
            }

            // Lido antes de pedir o ticket: token trocado entre as duas leituras não pode ficar sem aviso.
            var tokenChanged = fleet.TokenChanged;
            TimeSpan? wait = null;
            try
            {
                var ticket = await fleet.GetRealtimeTicketAsync(stoppingToken).ConfigureAwait(false);
                if (ticket is null)
                {
                    // Servidor sem tempo real, ou recusou.
                    openFailures++;
                    wait = OpenWait(openFailures);
                }
                else
                {
                    var outcome = await RunConnectionAsync(ticket, tokenChanged, stoppingToken).ConfigureAwait(false);
                    if (outcome.Reason == EndReason.Stopped) return;

                    if (outcome.Duration >= StableConnection) dropFailures = 0;

                    switch (outcome.Reason)
                    {
                        case EndReason.Rejected:
                            openFailures++;
                            wait = OpenWait(openFailures);
                            break;
                        case EndReason.ServerClosed:
                        case EndReason.Dropped:
                            openFailures = 0;
                            var w = BackoffFor(dropFailures);
                            // 1012 (reinício) e 1013 (tente depois): o servidor pediu calma.
                            if (outcome.CloseStatus is { } cs && ((int)cs == 1012 || (int)cs == 1013)
                                && w < TimeSpan.FromSeconds(30))
                                w = TimeSpan.FromSeconds(30);
                            dropFailures++;
                            wait = w;
                            if (dropFailures >= 3)
                                log.LogWarning("Tempo real do monitoramento caindo repetidamente ({Falhas} quedas, status {Status})",
                                    dropFailures, outcome.CloseStatus);
                            break;
                        default: // Renew, TokenChanged: reconecta na hora
                            openFailures = 0;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (tokenChanged.IsCancellationRequested)
            {
                // Token trocado durante a abertura: reconecta na hora com o novo.
            }
            catch (Exception ex)
            {
                openFailures++;
                wait = OpenWait(openFailures);
                log.LogDebug(ex, "Tempo real do monitoramento não conectou");
            }

            if (wait is { } delay && !await DelayAsync(delay, stoppingToken)) return;
        }
    }

    private async Task<Outcome> RunConnectionAsync(FleetRealtimeTicket ticket, CancellationToken tokenChanged, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, tokenChanged);
        linked.CancelAfter(RenewDelay(ticket.ExpiresAt, DateTimeOffset.UtcNow, RenewBefore, MinRenew, RenewFallback));
        var ct = linked.Token;

        // Renovação/troca de token durante a abertura não é falha: reconecta na hora.
        bool Interrupted() => ct.IsCancellationRequested && !stoppingToken.IsCancellationRequested;
        Outcome InterruptedOutcome() => new(tokenChanged.IsCancellationRequested ? EndReason.TokenChanged : EndReason.Renew, null, TimeSpan.Zero);

        // Outras falhas até aqui (conectar, assinar) propagam: contam como falha de abertura.
        WebSocket ws;
        try { ws = await Connect(ticket.WsUrl, ticket.Token, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (Interrupted()) { return InterruptedOutcome(); }
        using var wsLease = ws;
        var subscribe = JsonSerializer.Serialize(new { action = "subscribe", rooms = new[] { ticket.Room } });
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(subscribe)), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Interrupted()) { return InterruptedOutcome(); }

        var clock = Stopwatch.StartNew();
        EndReason? reason = null;
        WebSocketCloseStatus? closeStatus = null;

        fleet.SetRealtimeConnected(true);
        log.LogInformation("Tempo real do monitoramento conectado");
        RequestPoll(stoppingToken); // pega o que chegou enquanto estava desconectado

        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        var skip = false;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    reason = EndReason.ServerClosed;
                    closeStatus = result.CloseStatus ?? ws.CloseStatus;
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text) skip = true;
                if (!skip)
                {
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        skip = true;
                        message.SetLength(0);
                    }
                }

                if (!result.EndOfMessage) continue;

                if (!skip)
                {
                    var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    if (IsSystemError(json, out var error))
                    {
                        log.LogWarning("Hub do monitoramento recusou a assinatura: {Mensagem}", error);
                        reason = EndReason.Rejected;
                        break;
                    }

                    if (IsCommandSignal(json))
                    {
                        Interlocked.Increment(ref _signals);
                        RequestPoll(stoppingToken);
                    }
                }

                skip = false;
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = stoppingToken.IsCancellationRequested ? EndReason.Stopped
                : tokenChanged.IsCancellationRequested ? EndReason.TokenChanged
                : EndReason.Renew;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Tempo real do monitoramento caiu");
            if (!ct.IsCancellationRequested) reason = EndReason.Dropped; // senão: aborto causado pelo cancelamento
        }
        finally
        {
            fleet.SetRealtimeConnected(false);
        }

        reason ??= stoppingToken.IsCancellationRequested ? EndReason.Stopped
            : tokenChanged.IsCancellationRequested ? EndReason.TokenChanged
            : ct.IsCancellationRequested ? EndReason.Renew
            : EndReason.Dropped;

        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token).ConfigureAwait(false);
            }
            catch { /* já caiu */ }
        }

        return new Outcome(reason.Value, closeStatus, clock.Elapsed);
    }

    /// <summary>Vários avisos seguidos viram uma busca só (mais uma se chegar outro durante a busca).</summary>
    private void RequestPoll(CancellationToken ct)
    {
        Interlocked.Exchange(ref _pollDirty, 1);
        if (Interlocked.CompareExchange(ref _pollRunning, 1, 0) == 0) _ = PumpAsync(ct);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (Interlocked.Exchange(ref _pollDirty, 0) == 1)
                await PollSafeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _pollRunning, 0);
            // Aviso que chegou entre o último teste do laço e o fim: não pode ficar sem busca.
            if (Volatile.Read(ref _pollDirty) == 1 && Interlocked.CompareExchange(ref _pollRunning, 1, 0) == 0)
                _ = PumpAsync(ct);
        }
    }

    private async Task PollSafeAsync(CancellationToken ct)
    {
        try { await poller.PollAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { log.LogDebug(ex, "Busca de comandos falhou"); }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Delay(delay, ct).ConfigureAwait(false);
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
