using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface IJobRepository
{
    IReadOnlyList<PrintJob> Snapshot();
    /// <summary>Primeiro pendente sem clonar a fila inteira.</summary>
    PrintJob? PeekNextPending();
    PrintJob? FindByReference(string reference);
    PrintJob? FindById(Guid id);
    PrintJob Add(PrintJob job);
    PrintJob? TakeNextPending(PrintOptions settings);
    PrintJob? TakeNextPending(string printerName, long settingsRevision);
    void AppendStep(Guid id, string stage, string where, string message, string? detail = null, bool isError = false);
    /// <summary>Vários passos com uma única gravação em disco.</summary>
    void AppendSteps(Guid id, IReadOnlyList<JobStepDraft> steps);
    void Finish(Guid id, JobStatus status, string? error = null, string? errorReason = null, string? errorWhere = null);
    int PurgeOlderThan(DateTimeOffset cutoff);
}

public readonly record struct JobStepDraft(
    string Stage,
    string Where,
    string Message,
    string? Detail = null,
    bool IsError = false);
