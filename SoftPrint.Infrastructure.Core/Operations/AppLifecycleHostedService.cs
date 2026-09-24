using SoftPrint.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SoftPrint.Infrastructure.Operations;

/// <summary>Liga o ciclo de vida do host aos logs de sessão (início/fim).</summary>
public sealed class AppLifecycleHostedService(
    IAppLifecycleLogger lifecycle,
    ILogger<AppLifecycleHostedService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            lifecycle.OnApplicationStarted();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao registrar ciclo de vida do SoftPrint.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            lifecycle.OnApplicationStopping();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha ao gravar encerramento do SoftPrint.");
        }

        return Task.CompletedTask;
    }
}
