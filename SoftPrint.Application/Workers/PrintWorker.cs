using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using Microsoft.Extensions.Options;

namespace SoftPrint.Application.Workers;

public sealed class PrintWorker(
    IJobRepository jobs,
    ISettingsRepository settings,
    ISystemSettingsRepository systemSettings,
    IPrinterRouter router,
    IPrinterCatalog printers,
    PrintStrategyResolver strategies,
    IWebhookNotifier webhook,
    IAppNotifier notifier,
    ITelemetryService telemetry,
    IOptions<SoftPrintFeatureOptions> features,
    ILogger<PrintWorker> logger) : BackgroundService
{
    private static readonly HttpClient _webhookClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Balloon grouping: printer name → (count, timestamp of first success in window)
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset FirstAt)> _balloonGroups = new();
    private readonly object _balloonGate = new();

    // Rate limiting: inbox entry id → sliding window of print timestamps
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _rateWindows = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            var pollInterval = Math.Clamp(systemSettings.Current.PollIntervalMs, 100, 60_000);
            var options = settings.Current;
            if (options.Paused)
            {
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            var pending = jobs.PeekNextPending();
            if (pending is null)
            {
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            // Rate limit check: if the inbox entry for this job is rate-limited, skip without dequeuing
            var matchingEntryForPeek = options.InboxEntries.FirstOrDefault(e =>
                InboxFileRules.IsUnderInbox(pending.SourcePath, e.Folder));
            if (matchingEntryForPeek is not null && IsRateLimited(matchingEntryForPeek))
            {
                await Task.Delay(pollInterval, stoppingToken);
                continue;
            }

            var printer = !string.IsNullOrWhiteSpace(pending.RequestedPrinterName)
                ? pending.RequestedPrinterName
                : router.ResolvePrinter(pending.JobType, options.PrinterName);
            var routed = !string.Equals(printer, options.PrinterName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(printer);

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
                var eff = pending.SettingsOverride;
                var matchingEntry = options.InboxEntries.FirstOrDefault(e =>
                    InboxFileRules.IsUnderInbox(pending.SourcePath, e.Folder));
                var copies = matchingEntry is not null ? Math.Clamp(matchingEntry.Copies, 1, 99) : 1;
                var printOptions = new PrintOptions
                {
                    PrinterName = printer,
                    Simulation = options.Simulation,
                    Paused = options.Paused,
                    Revision = options.Revision,
                    UpdatedAt = options.UpdatedAt,
                    ImageFit = eff?.ImageFit ?? options.ImageFit,
                    ImageScalePercent = eff?.ImageScalePercent ?? options.ImageScalePercent,
                    PaperSize = eff?.PaperSize ?? options.PaperSize,
                    PaperWidthMm = eff?.PaperWidthMm ?? options.PaperWidthMm,
                    PaperHeightMm = eff?.PaperHeightMm ?? options.PaperHeightMm,
                    PaperLandscape = eff?.PaperLandscape ?? options.PaperLandscape,
                    Copies = copies
                };

                if (!printOptions.Simulation && IsPrinterUnavailable(printer, out var printerDetail))
                {
                    status = JobStatus.Uncertain;
                    error = $"Impressora indisponível: {printer}";
                    errorReason = printerDetail;
                    errorWhere = "PrintWorker → catálogo de impressoras";
                    jobs.AppendStep(job.Id, "printer-check", "PrintWorker",
                        error, errorReason, isError: true);
                }
                else
                {
                    var strategy = strategies.Resolve(job, printOptions);
                    var strategyName = strategy.GetType().Name;
                    var mode = printOptions.Simulation ? "simulação (sem papel)" : $"conteúdo {job.ContentKind.ToWire()}";

                    var timeoutSec = Math.Clamp(features.Value.PrintJobTimeoutSeconds, 30, 900);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                    try
                    {
                        status = await strategy.ExecuteAsync(job, printOptions, timeoutCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        status = JobStatus.Uncertain;
                        error = $"Impressão excedeu {timeoutSec}s (timeout).";
                        errorReason =
                            "O spooler ou a estratégia não respondeu a tempo. A fila segue com o próximo pedido.";
                        errorWhere = $"PrintWorker → {strategyName}";
                        logger.LogWarning(
                            "Timeout de {Seconds}s no pedido {JobId} ({Strategy})",
                            timeoutSec, job.Id, strategyName);
                    }

                    var steps = new List<JobStepDraft>(4);
                    if (routed)
                    {
                        steps.Add(new JobStepDraft(
                            "route", "PrinterRouter",
                            $"Roteado pelo tipo '{job.JobType}'.",
                            $"Impressora padrão '{options.PrinterName}' → '{printer}'"));
                    }

                    steps.Add(new JobStepDraft(
                        "layout", "PrintWorker",
                        $"Layout capturado: {PrintSurfaceMapper.Describe(printOptions)}.",
                        printOptions.Simulation
                            ? "A simulação usa a mesma configuração de papel/encaixe do envio real."
                            : "O spooler recebe este papel, encaixe e escala — os mesmos do preview."));
                    steps.Add(new JobStepDraft(
                        "strategy", $"PrintWorker → {strategyName}",
                        $"Estratégia selecionada: {mode}.",
                        printOptions.Simulation
                            ? "SimulationPrintStrategy marca como simulado."
                            : $"Enviando via {strategyName} para '{printer}'."));
                    if (status is JobStatus.Sent or JobStatus.Simulated)
                    {
                        steps.Add(new JobStepDraft(
                            "executed", strategyName,
                            printOptions.Simulation ? "Simulação concluída." : "Impressão enviada ao spooler.",
                            $"Pedido {job.Reference} • tipo {job.JobType}"));
                    }
                    else if (status == JobStatus.Uncertain && error is not null)
                    {
                        steps.Add(new JobStepDraft(
                            "timeout", strategyName, error, errorReason, IsError: true));
                    }

                    jobs.AppendSteps(job.Id, steps);
                }
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
            else
            {
                NotifyCompletedGrouped(finished, printer);
            }
            _ = webhook.NotifyFinishedAsync(finished, CancellationToken.None);

            var entryForWebhook = options.InboxEntries.FirstOrDefault(e =>
                InboxFileRules.IsUnderInbox(finished.SourcePath, e.Folder));
            if (!string.IsNullOrWhiteSpace(entryForWebhook?.WebhookUrl))
                _ = TryFireEntryWebhookAsync(entryForWebhook!.WebhookUrl!, finished, status);
        }
    }

    private bool IsRateLimited(InboxEntry entry)
    {
        if (entry.RateLimitPerMinute <= 0) return false;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-1);
        var window = _rateWindows.GetOrAdd(entry.Id, _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && window.Peek() < cutoff)
                window.Dequeue();
            if (window.Count >= entry.RateLimitPerMinute)
                return true;
            window.Enqueue(now);
            return false;
        }
    }

    private void NotifyCompletedGrouped(PrintJob job, string printerName)
    {
        var now = DateTimeOffset.UtcNow;
        const double windowSeconds = 5.0;

        lock (_balloonGate)
        {
            if (_balloonGroups.TryGetValue(printerName, out var existing))
            {
                if ((now - existing.FirstAt).TotalSeconds <= windowSeconds)
                {
                    _balloonGroups[printerName] = (existing.Count + 1, existing.FirstAt);
                    return;
                }
            }

            _balloonGroups[printerName] = (1, now);
        }

        // Schedule the flush after the grouping window
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(windowSeconds));
            int count;
            lock (_balloonGate)
            {
                if (!_balloonGroups.TryGetValue(printerName, out var g))
                    return;
                count = g.Count;
                _balloonGroups.TryRemove(printerName, out _);
            }
            var label = string.IsNullOrWhiteSpace(printerName) ? "impressora" : printerName;
            notifier.NotifyPrintBatch(label, count);
        });
    }

    private async Task TryFireEntryWebhookAsync(string url, PrintJob job, JobStatus jobStatus)
    {
        try
        {
            var statusStr = jobStatus == JobStatus.Uncertain ? "failure" : "success";
            var errorStr = jobStatus == JobStatus.Uncertain ? (job.Error ?? job.ErrorReason) : null;
            var payload = new
            {
                jobId = job.Id,
                file = job.SourcePath ?? job.Reference,
                printer = job.PrinterName ?? "",
                status = statusStr,
                error = errorStr,
                timestamp = DateTimeOffset.UtcNow
            };
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _webhookClient.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Webhook de entrada retornou HTTP {Code} para job {JobId}", (int)response.StatusCode, job.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha no webhook de entrada para job {JobId}", job.Id);
        }
    }

    private bool IsPrinterUnavailable(string printerName, out string detail)
    {
        detail = "";
        if (string.IsNullOrWhiteSpace(printerName))
        {
            detail = "Nenhuma impressora selecionada.";
            return true;
        }

        try
        {
            var match = printers.ListDetailed()
                .FirstOrDefault(p => string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                detail = $"'{printerName}' não está instalada neste Windows.";
                return true;
            }

            if (match.IsOffline)
            {
                detail = match.Status is { Length: > 0 } s
                    ? s
                    : "Windows reporta a impressora como offline/ausente.";
                return true;
            }

            var status = match.Status ?? "";
            if (status.Contains("Offline", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("Parada", StringComparison.OrdinalIgnoreCase))
            {
                detail = status;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Falha ao consultar catálogo de impressoras");
            return false; // não bloquear se o catálogo falhar
        }
    }

    private void TryDeleteInboxSource(PrintOptions options, PrintJob job, JobStatus status)
    {
        if (status != JobStatus.Sent)
            return;
        var matchingEntry = options.InboxEntries.FirstOrDefault(e =>
            e.DeleteAfterPrint && InboxFileRules.IsUnderInbox(job.SourcePath, e.Folder));
        if (matchingEntry is null)
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
