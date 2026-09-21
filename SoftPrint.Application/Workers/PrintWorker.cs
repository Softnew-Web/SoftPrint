using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using Microsoft.Extensions.Options;

namespace SoftPrint.Application.Workers;

public sealed class PrintWorker(
    IJobRepository jobs,
    ISettingsRepository settings,
    IPrinterRouter router,
    PrintStrategyResolver strategies,
    IWebhookNotifier webhook,
    IAppNotifier notifier,
    ITelemetryService telemetry,
    IOptions<SoftPrintFeatureOptions> features,
    IConfiguration configuration,
    ILogger<PrintWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var pollInterval = Math.Clamp(
            configuration.GetValue("SoftPrint:PollIntervalMs", features.Value.PollIntervalMs),
            100, 60_000);

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = settings.Current;
            if (options.Paused)
            {
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            var pending = jobs.Snapshot().FirstOrDefault(j => j.Status == JobStatus.Pending);
            if (pending is null)
            {
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            var printer = router.ResolvePrinter(pending.JobType, options.PrinterName);
            if (!string.Equals(printer, options.PrinterName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(printer))
            {
                jobs.AppendStep(pending.Id, "route", "PrinterRouter",
                    $"Roteado pelo tipo '{pending.JobType}'.",
                    $"Impressora padrão '{options.PrinterName}' → '{printer}'");
            }

            var job = jobs.TakeNextPending(printer, options.Revision);
            if (job is null)
            {
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            JobStatus status;
            string? error = null;
            string? errorReason = null;
            string? errorWhere = null;
            try
            {
                var printOptions = new PrintOptions
                {
                    PrinterName = printer,
                    Simulation = options.Simulation,
                    Paused = options.Paused,
                    Revision = options.Revision,
                    UpdatedAt = options.UpdatedAt,
                    ImageFit = options.ImageFit,
                    ImageScalePercent = options.ImageScalePercent,
                    PaperSize = options.PaperSize,
                    PaperWidthMm = options.PaperWidthMm,
                    PaperHeightMm = options.PaperHeightMm,
                    PaperLandscape = options.PaperLandscape
                };

                var strategy = strategies.Resolve(job, printOptions);
                var strategyName = strategy.GetType().Name;
                var mode = printOptions.Simulation ? "simulação (sem papel)" : $"conteúdo {job.ContentKind.ToWire()}";
                jobs.AppendStep(job.Id, "strategy", $"PrintWorker → {strategyName}",
                    $"Estratégia selecionada: {mode}.",
                    printOptions.Simulation
                        ? "SimulationPrintStrategy marca como simulado."
                        : $"Enviando via {strategyName} para '{printer}'.");

                jobs.AppendStep(job.Id, "executing", strategyName,
                    printOptions.Simulation ? "Executando simulação…" : "Executando impressão…",
                    $"Pedido {job.Reference} • tipo {job.JobType}");

                status = await strategy.ExecuteAsync(job, printOptions, stoppingToken);
            }
            catch (Exception exception)
            {
                status = JobStatus.Uncertain;
                (error, errorReason, errorWhere) = PrintFailureExplainer.Explain(
                    exception, "PrintWorker → estratégia de impressão");
                logger.LogError(exception, "Falha ao enviar trabalho {JobId}", job.Id);
            }

            jobs.Finish(job.Id, status, error, errorReason, errorWhere);
            var finished = jobs.FindById(job.Id) ?? job;

            TryDeleteInboxSource(options, finished, status);

            if (status == JobStatus.Uncertain)
            {
                notifier.NotifyUncertain(finished);
                _ = telemetry.ReportPrintFailureAsync(finished, CancellationToken.None);
            }
            else notifier.NotifyCompleted(finished);
            await webhook.NotifyFinishedAsync(finished, stoppingToken);
        }
    }

    private void TryDeleteInboxSource(PrintOptions options, PrintJob job, JobStatus status)
    {
        // Só apaga após envio real. Simulação / falha / conferir: arquivo permanece.
        if (!options.DeleteInboxAfterPrint || status != JobStatus.Sent)
            return;
        if (!InboxFileRules.IsUnderInbox(job.SourcePath, options.InboxFolder))
            return;
        if (string.IsNullOrWhiteSpace(job.SourcePath) || !File.Exists(job.SourcePath))
            return;

        try
        {
            File.Delete(job.SourcePath);
            jobs.AppendStep(job.Id, "cleanup", "PrintWorker → pasta de entrada",
                "Arquivo removido da pasta após impressão.",
                job.SourcePath);
            logger.LogInformation("Arquivo da pasta de entrada apagado após impressão: {Path}", job.SourcePath);
        }
        catch (Exception ex)
        {
            jobs.AppendStep(job.Id, "cleanup", "PrintWorker → pasta de entrada",
                "Impressão ok, mas não foi possível apagar o arquivo.",
                ex.Message, isError: true);
            logger.LogWarning(ex, "Falha ao apagar arquivo da pasta de entrada {Path}", job.SourcePath);
        }
    }
}
