using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Infrastructure.Integrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Operations;

/// <summary>
/// Heartbeat para o monitoramento Softnew. O servidor marca a máquina offline depois de
/// ~2 min sem contato; ao fechar normalmente, avisa "stopping" para não gerar alerta.
/// </summary>
public sealed class FleetHeartbeatHostedService(
    IFleetService fleet,
    IOptions<SoftPrintFeatureOptions> options,
    ILogger<FleetHeartbeatHostedService> log) : BackgroundService
{
    private volatile bool _announced;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Lido a cada volta: token salvo na tela vale sem reiniciar.
            if (fleet.GetStatus().Configured)
            {
                try
                {
                    var result = await fleet.SendHeartbeatAsync("heartbeat", stoppingToken).ConfigureAwait(false);
                    if (result.Ok) _announced = true;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Heartbeat do monitoramento falhou");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(FleetService.HeartbeatSeconds(options.Value)), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
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
