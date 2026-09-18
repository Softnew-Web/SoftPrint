using SoftPrint.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SoftPrint.Infrastructure.Updates;

/// <summary>Consulta o GitHub em background para o painel não esperar na primeira abertura.</summary>
public sealed class UpdateCheckHostedService : BackgroundService
{
    private readonly IUpdateChecker _checker;
    private readonly ILogger<UpdateCheckHostedService> _logger;

    public UpdateCheckHostedService(IUpdateChecker checker, ILogger<UpdateCheckHostedService> logger)
    {
        _checker = checker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            var result = await _checker.CheckAsync(stoppingToken).ConfigureAwait(false);
            if (result.Error is not null)
                _logger.LogInformation("Verificação de atualização: {Error}", result.Error);
            else if (result.UpdateAvailable)
                _logger.LogInformation(
                    "Atualização disponível: {Current} → {Latest} (obrigatória={Mandatory})",
                    result.CurrentVersion, result.LatestVersion, result.Mandatory);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha na verificação inicial de atualização");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken).ConfigureAwait(false);
                await _checker.CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha na verificação periódica de atualização");
            }
        }
    }
}
