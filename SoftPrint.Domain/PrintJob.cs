namespace SoftPrint.Domain;

public sealed class PrintJob
{
    private readonly List<ProcessingStep> _steps = [];

    public Guid Id { get; init; }
    public string Reference { get; init; } = "";
    public string Text { get; init; } = "";
    public string JobType { get; init; } = "default";
    public JobContentKind ContentKind { get; init; } = JobContentKind.Text;
    public string? SourcePath { get; init; }
    public string? TemplateName { get; init; }
    public JobStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorReason { get; private set; }
    public string? ErrorWhere { get; private set; }
    public string? PrinterName { get; private set; }
    public long? SettingsRevision { get; private set; }
    public Guid? ReprintedFromId { get; init; }
    public IReadOnlyList<ProcessingStep> Steps => _steps;

    public PrintJob()
    {
    }

    public PrintJob(
        Guid id,
        string reference,
        string text,
        JobStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? finishedAt = null,
        string? error = null,
        string? printerName = null,
        long? settingsRevision = null,
        IEnumerable<ProcessingStep>? steps = null,
        string? errorReason = null,
        string? errorWhere = null,
        string jobType = "default",
        JobContentKind contentKind = JobContentKind.Text,
        string? sourcePath = null,
        string? templateName = null,
        Guid? reprintedFromId = null)
    {
        Id = id;
        Reference = reference;
        Text = text;
        JobType = string.IsNullOrWhiteSpace(jobType) ? "default" : jobType.Trim();
        ContentKind = contentKind;
        SourcePath = sourcePath;
        TemplateName = templateName;
        Status = status;
        CreatedAt = createdAt;
        FinishedAt = finishedAt;
        Error = error;
        ErrorReason = errorReason;
        ErrorWhere = errorWhere;
        PrinterName = printerName;
        SettingsRevision = settingsRevision;
        ReprintedFromId = reprintedFromId;
        if (steps is not null) _steps.AddRange(steps);
    }

    public static PrintJob CreatePending(
        string reference,
        string text,
        string jobType = "default",
        JobContentKind contentKind = JobContentKind.Text,
        string? sourcePath = null,
        string? templateName = null,
        Guid? reprintedFromId = null)
    {
        var job = new PrintJob(
            Guid.NewGuid(), reference, text, JobStatus.Pending, DateTimeOffset.UtcNow,
            jobType: jobType, contentKind: contentKind, sourcePath: sourcePath,
            templateName: templateName, reprintedFromId: reprintedFromId);

        var detail = $"Tipo: {job.JobType} • Conteúdo: {contentKind.ToWire()}"
                     + (templateName is null ? "" : $" • Template: {templateName}")
                     + (reprintedFromId is null ? "" : $" • Reimpressão de {reprintedFromId}");
        job.AddStep(
            "received",
            "API → JobQueueService",
            reprintedFromId is null ? "Pedido recebido e validado." : "Reimpressão criada e colocada na fila.",
            detail);
        return job;
    }

    public void AddStep(string stage, string where, string message, string? detail = null, bool isError = false) =>
        _steps.Add(new ProcessingStep(DateTimeOffset.UtcNow, stage, where, message, detail, isError));

    public void MarkProcessing(string printerName, long settingsRevision)
    {
        EnsureStatus(JobStatus.Pending);
        Status = JobStatus.Processing;
        PrinterName = printerName;
        SettingsRevision = settingsRevision;
        AddStep(
            "claimed",
            "PrintWorker",
            "Pedido retirado da fila para processamento.",
            string.IsNullOrWhiteSpace(printerName)
                ? $"Tipo {JobType} • revision {settingsRevision} (sem impressora — simulação esperada)."
                : $"Tipo {JobType} → impressora '{printerName}' • revision {settingsRevision}");
    }

    public void MarkFinished(JobStatus status, string? error = null, string? errorReason = null, string? errorWhere = null)
    {
        if (status is not (JobStatus.Simulated or JobStatus.Sent or JobStatus.Uncertain))
            throw new InvalidOperationException("Status final inválido.");

        Status = status;
        Error = error;
        ErrorReason = errorReason;
        ErrorWhere = errorWhere;
        FinishedAt = DateTimeOffset.UtcNow;

        if (status == JobStatus.Uncertain)
        {
            AddStep(
                "error",
                errorWhere ?? "PrintWorker",
                error ?? "Falha no envio.",
                errorReason ?? "Confira a impressora antes de reenviar com uma nova referência.",
                isError: true);
        }
        else
        {
            AddStep(
                "done",
                status == JobStatus.Simulated ? "SimulationPrintStrategy" : "estratégia de impressão → spooler",
                status == JobStatus.Simulated
                    ? "Simulação concluída. Nenhum papel foi usado."
                    : "Conteúdo enviado ao Windows (sent ≠ papel saiu).",
                $"Status final: {status.ToDisplay()} • tipo {JobType}");
        }
    }

    public void MarkInterrupted()
    {
        if (Status != JobStatus.Processing) return;
        Status = JobStatus.Uncertain;
        Error = "Aplicativo interrompido durante o envio. Confira a impressora antes de reenviar.";
        ErrorReason = "O SoftPrint foi fechado ou reiniciado no meio do envio. O papel pode ou não ter saído — confira antes de reenviar.";
        ErrorWhere = "PrintWorker (reinício)";
        FinishedAt ??= DateTimeOffset.UtcNow;
        AddStep("error", ErrorWhere, Error, ErrorReason, isError: true);
    }

    private void EnsureStatus(JobStatus expected)
    {
        if (Status != expected)
            throw new InvalidOperationException($"Trabalho {Id} deveria estar {expected.ToWire()}, mas está {Status.ToWire()}.");
    }
}
