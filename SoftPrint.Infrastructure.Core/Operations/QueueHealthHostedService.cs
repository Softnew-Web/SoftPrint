using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SoftPrint.Infrastructure.Operations;

/// <summary>Avisa na bandeja se a fila ficar presa ou pausada com pedidos pendentes.</summary>
public sealed class QueueHealthHostedService(
    IJobRepository jobs,
    ISettingsRepository settings,
    IAppNotifier notifier,
    ILogger<QueueHealthHostedService> logger) : BackgroundService
{
    private DateTimeOffset? _lastStallNotify;
    private DateTimeOffset? _lastPauseNotify;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckOnce();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Falha no monitoramento da fila");
            }

            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
        }
    }

    private void CheckOnce()
    {
        var snapshot = jobs.Snapshot();
        var pending = snapshot.Where(j => j.Status is JobStatus.Pending or JobStatus.Processing).ToArray();
        var options = settings.Current;
        var now = DateTimeOffset.UtcNow;

        if (options.Paused && pending.Length > 0)
        {
            if (_lastPauseNotify is null || now - _lastPauseNotify > TimeSpan.FromMinutes(30))
            {
                _lastPauseNotify = now;
                notifier.NotifyQueueAlert(
                    "SoftPrint — fila pausada",
                    $"{pending.Length} pedido(s) aguardando. Retome a fila no painel.");
            }
            return;
        }

        var oldest = pending
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefault();
        if (oldest is null) return;

        var age = now - oldest.CreatedAt.ToUniversalTime();
        if (age < TimeSpan.FromMinutes(10)) return;

        if (_lastStallNotify is null || now - _lastStallNotify > TimeSpan.FromMinutes(20))
        {
            _lastStallNotify = now;
            notifier.NotifyQueueAlert(
                "SoftPrint — fila parada",
                $"Pedido {oldest.Reference} há {(int)age.TotalMinutes} min. Confira impressora e erros.");
        }
    }
}
