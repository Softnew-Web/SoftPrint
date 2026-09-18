using AutoPrint.Domain;

namespace AutoPrint.Application.Abstractions;

public interface IJobRepository
{
    IReadOnlyList<PrintJob> Snapshot();
    PrintJob? FindByReference(string reference);
    PrintJob? FindById(Guid id);
    PrintJob Add(PrintJob job);
    PrintJob? TakeNextPending(PrintOptions settings);
    PrintJob? TakeNextPending(string printerName, long settingsRevision);
    void AppendStep(Guid id, string stage, string where, string message, string? detail = null, bool isError = false);
    void Finish(Guid id, JobStatus status, string? error = null, string? errorReason = null, string? errorWhere = null);
    int PurgeOlderThan(DateTimeOffset cutoff);
}
