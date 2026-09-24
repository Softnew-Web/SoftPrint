using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Operations;

/// <summary>Heartbeat opt-in para frota (versão, fila, último erro) — sem PHI.</summary>
public sealed class TelemetryHeartbeatHostedService(
    ITelemetryService telemetry,
    JobQueueService jobs,
    IOptions<SoftPrintFeatureOptions> options,
    ILogger<TelemetryHeartbeatHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Atraso inicial para não competir com o splash/update.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var hours = Math.Max(0, options.Value.TelemetryHeartbeatHours);
            if (hours <= 0)
            {
                try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                continue;
            }

            try
            {
                var all = jobs.List();
                var pending = all.Count(j => j.Status == JobStatus.Pending);
                var processing = all.Count(j => j.Status == JobStatus.Processing);
                var lastError = all
                    .Where(j => j.Status == JobStatus.Uncertain)
                    .OrderByDescending(j => j.FinishedAt)
                    .Select(j => j.Error)
                    .FirstOrDefault();

                await telemetry.ReportHeartbeatAsync(pending, processing, lastError, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Heartbeat de telemetria falhou");
            }

            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
