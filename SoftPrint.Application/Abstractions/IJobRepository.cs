using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface IJobRepository
{
    IReadOnlyList<PrintJob> Snapshot();
    /// <summary>Primeiro pendente sem clonar a fila inteira.</summary>
    PrintJob? PeekNextPending();
    /// <summary>Pendentes na ordem em que seriam impressos (prioridade, depois posição na fila).</summary>
    IReadOnlyList<PrintJob> PendingInOrder() =>
        Snapshot().Where(j => j.Status == JobStatus.Pending).OrderByDescending(j => j.Priority).ToArray();
    PrintJob? FindByReference(string reference);
    PrintJob? FindById(Guid id);
    PrintJob Add(PrintJob job);
    PrintJob? TakeNextPending(PrintOptions settings);
    PrintJob? TakeNextPending(string printerName, long settingsRevision);
    /// <summary>
    /// Retira exatamente este pedido, se ainda estiver pendente. O worker decide impressora e
    /// papel olhando o pedido; pegar "o próximo" poderia trazer outro (prioridade/reordenação).
    /// </summary>
    PrintJob? TakePending(Guid id, string printerName, long settingsRevision);
    void AppendStep(Guid id, string stage, string where, string message, string? detail = null, bool isError = false);
    /// <summary>Vários passos com uma única gravação em disco.</summary>
    void AppendSteps(Guid id, IReadOnlyList<JobStepDraft> steps);
    void Finish(Guid id, JobStatus status, string? error = null, string? errorReason = null, string? errorWhere = null);
    int PurgeOlderThan(DateTimeOffset cutoff);
    /// <summary>Reordena os jobs pendentes segundo a sequência de IDs fornecida.</summary>
    void ReorderPending(IEnumerable<Guid> ids) { }
    void SetJobPriority(Guid id, int priority) { }
}

public readonly record struct JobStepDraft(
    string Stage,
    string Where,
    string Message,
    string? Detail = null,
    bool IsError = false);
