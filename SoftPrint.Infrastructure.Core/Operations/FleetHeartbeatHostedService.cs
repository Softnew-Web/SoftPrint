using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Operations;

/// <summary>
/// Heartbeat para o monitoramento Softnew. O servidor marca a máquina offline depois de
/// ~2 min sem contato; ao fechar normalmente, avisa "stopping" para não gerar alerta.
/// Com o WebSocket conectado, o intervalo é o configurado; sem ele, 15 s (é quando os comandos
/// chegam por aqui). Mudou a fila? Manda na hora (2 s para juntar mudanças, no máximo um a cada 5 s).
/// </summary>
public sealed class FleetHeartbeatHostedService(
    IFleetService fleet,
    IFleetCommandPoller poller,
    IPendingUpdateReporter updateReporter,
    JobQueueService jobs,
    ISettingsRepository settings,
    IOptions<SoftPrintFeatureOptions> options,
    ILogger<FleetHeartbeatHostedService> log) : BackgroundService
{
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(5);

    // Ajustáveis só pelos testes.
    internal TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan? IntervalOverride { get; init; }

    private volatile bool _announced;
    private int _commandWorkRunning;

    public static bool ShouldSend(DateTimeOffset now, DateTimeOffset? lastSent, DateTimeOffset? changedAt, TimeSpan interval)
    {
        if (lastSent is null) return true;
        var sinceSent = now - lastSent.Value;
        if (sinceSent >= interval) return true;
        return changedAt is not null && now - changedAt.Value >= Settle && sinceSent >= MinGap;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        DateTimeOffset? lastSent = null;
        DateTimeOffset? changedAt = null;
        string? lastFingerprint = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var status = fleet.GetStatus();
            if (status.Configured)
            {
                var now = DateTimeOffset.UtcNow;
                var fingerprint = Fingerprint();
                if (lastFingerprint is not null && fingerprint != lastFingerprint && changedAt is null)
                    changedAt = now;
                lastFingerprint = fingerprint;

                var configured = TimeSpan.FromSeconds(FleetService.HeartbeatSeconds(options.Value));
                var interval = IntervalOverride ?? (status.RealtimeConnected ? configured : FallbackInterval);

                if (ShouldSend(now, lastSent, changedAt, interval))
                {
                    lastSent = now;
                    changedAt = null;
                    try
                    {
                        var result = await fleet.SendHeartbeatAsync("heartbeat", stoppingToken).ConfigureAwait(false);
                        if (result.Ok)
                        {
                            _announced = true;
                            var needReport = HasPendingUpdate();
                            var needPoll = result.PendingCommands > 0;
                            if (needReport || needPoll) StartCommandWork(needReport, needPoll, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        log.LogDebug(ex, "Heartbeat do monitoramento falhou");
                    }
                }
            }

            try { await Task.Delay(Tick, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private bool HasPendingUpdate()
    {
        try { return fleet.GetPendingUpdate() is not null; }
        catch { return false; }
    }

    /// <summary>
    /// Comando pode demorar (pacote de suporte sobe por minutos): roda em segundo plano para o heartbeat
    /// não parar. Já rodando? Ignora; o próximo heartbeat vê "pending_commands" de novo.
    /// </summary>
    private void StartCommandWork(bool needReport, bool needPoll, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _commandWorkRunning, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (needReport) await updateReporter.ReportPendingUpdateAsync(ct).ConfigureAwait(false);
                if (needPoll) await poller.PollAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogDebug(ex, "Comandos do monitoramento falharam");
            }
            catch (OperationCanceledException) { /* encerrando */ }
            finally
            {
                Volatile.Write(ref _commandWorkRunning, 0);
            }
        });
    }

    /// <summary>O que o painel mostra da fila: mudou, manda heartbeat.</summary>
    private string Fingerprint()
    {
        try
        {
            var all = jobs.List();
            var pending = all.Count(j => j.Status == JobStatus.Pending);
            var processing = all.Count(j => j.Status == JobStatus.Processing);
            var lastError = all.Where(j => j.Status == JobStatus.Uncertain)
                .OrderByDescending(j => j.FinishedAt).Select(j => j.Id).FirstOrDefault();
            return $"{pending}|{processing}|{lastError}|{settings.Current.Paused}";
        }
        catch
        {
            return "";
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!_announced || !fleet.GetStatus().Configured) return;

        // Fechamento normal: o painel mostra "parado" em vez de alertar queda.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await fleet.SendHeartbeatAsync("stopping", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Aviso de encerramento ao monitoramento falhou");
        }
    }
}
