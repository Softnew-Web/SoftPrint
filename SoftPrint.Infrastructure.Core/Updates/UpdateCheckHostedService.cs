using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Updates;

/// <summary>Consulta o GitHub em background; opcionalmente aplica atualização no arranque (modo bandeja).</summary>
public sealed class UpdateCheckHostedService : BackgroundService
{
    private readonly IUpdateChecker _checker;
    private readonly IUpdateApplier _applier;
    private readonly IOptionsMonitor<SoftPrintFeatureOptions> _options;
    private readonly ILogger<UpdateCheckHostedService> _logger;

    public UpdateCheckHostedService(
        IUpdateChecker checker,
        IUpdateApplier applier,
        IOptionsMonitor<SoftPrintFeatureOptions> options,
        ILogger<UpdateCheckHostedService> logger)
    {
        _checker = checker;
        _applier = applier;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            _checker.InvalidateCache();
            var result = await _checker.CheckAsync(stoppingToken).ConfigureAwait(false);
            if (result.Error is not null)
                _logger.LogInformation("Verificação de atualização: {Error}", result.Error);
            else if (result.UpdateAvailable)
            {
                _logger.LogInformation(
                    "Atualização disponível: {Current} → {Latest} (obrigatória={Mandatory})",
                    result.CurrentVersion, result.LatestVersion, result.Mandatory);

                // Splash já tenta aplicar no arranque com UI; aqui cobre modo bandeja/headless.
                if (_options.CurrentValue.AutoUpdateOnStartup &&
                    !string.IsNullOrWhiteSpace(result.DownloadUrl))
                {
                    if (_applier.TryStart(out var error))
                        _logger.LogInformation("Atualização automática iniciada em background.");
                    else if (!string.IsNullOrWhiteSpace(error))
                        _logger.LogDebug("Atualização automática não iniciada: {Error}", error);
                }
            }
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
