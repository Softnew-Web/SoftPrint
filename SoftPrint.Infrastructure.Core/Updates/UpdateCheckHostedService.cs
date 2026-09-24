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
    private readonly IAppNotifier _notifier;
    private readonly IOptionsMonitor<SoftPrintFeatureOptions> _options;
    private readonly ILogger<UpdateCheckHostedService> _logger;
    private string? _lastNotifiedLatest;

    public UpdateCheckHostedService(
        IUpdateChecker checker,
        IUpdateApplier applier,
        IAppNotifier notifier,
        IOptionsMonitor<SoftPrintFeatureOptions> options,
        ILogger<UpdateCheckHostedService> logger)
    {
        _checker = checker;
        _applier = applier;
        _notifier = notifier;
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
            NotifyIfNeeded(result);
            if (result.Error is not null)
                _logger.LogInformation("Verificação de atualização: {Error}", result.Error);
            else if (result.UpdateAvailable)
            {
                _logger.LogInformation(
                    "Atualização disponível: {Current} → {Latest} (obrigatória={Mandatory})",
                    result.CurrentVersion, result.LatestVersion, result.Mandatory);

                // Splash já aplica atualização no arranque com UI.
                // Em modo bandeja/headless, só atualiza sozinho se for obrigatória —
                // evita fechar o SoftPrint “do nada” por update opcional.
                if (_options.CurrentValue.AutoUpdateOnStartup &&
                    !string.IsNullOrWhiteSpace(result.DownloadUrl) &&
                    result.Mandatory)
                {
                    if (_applier.TryStart(out var error))
                        _logger.LogInformation("Atualização obrigatória iniciada em background.");
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
                var result = await _checker.CheckAsync(stoppingToken).ConfigureAwait(false);
                NotifyIfNeeded(result);
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

    private void NotifyIfNeeded(UpdateCheckResult result)
    {
        if (!result.UpdateAvailable || string.IsNullOrWhiteSpace(result.LatestVersion))
            return;
        if (string.Equals(_lastNotifiedLatest, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
            return;
        _lastNotifiedLatest = result.LatestVersion;
        try
        {
            _notifier.NotifyUpdateAvailable(result.CurrentVersion, result.LatestVersion!, result.Mandatory);
        }
        catch
        {
            /* ignore */
        }
    }
}
