using System.Text.Json;
using AutoPrint.Application.Abstractions;
using AutoPrint.Domain;

namespace AutoPrint.Infrastructure.Persistence;

public sealed class JsonJobRepository : IJobRepository
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<PrintJob> _jobs;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonJobRepository(IHostEnvironment environment)
    {
        var directory = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "jobs.json");
        _jobs = File.Exists(_path)
            ? (JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(_path), JsonOptions)
               ?? throw new InvalidDataException("Fila inválida.")).Select(ToDomain).ToList()
            : [];

        foreach (var job in _jobs)
            job.MarkInterrupted();
        Save();
    }

    public IReadOnlyList<PrintJob> Snapshot()
    {
        lock (_gate) return _jobs.Select(Clone).ToArray();
    }

    public PrintJob? FindByReference(string reference)
    {
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Reference == reference);
            return job is null ? null : Clone(job);
        }
    }

    public PrintJob? FindById(Guid id)
    {
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            return job is null ? null : Clone(job);
        }
    }

    public PrintJob Add(PrintJob job)
    {
        lock (_gate)
        {
            var existing = _jobs.FirstOrDefault(j => j.Reference == job.Reference);
            if (existing is not null) return Clone(existing);

            _jobs.Add(job);
            try { Save(); }
            catch
            {
                _jobs.Remove(job);
                throw;
            }
            return Clone(job);
        }
    }

    public PrintJob? TakeNextPending(PrintOptions settings)
    {
        lock (_gate)
        {
            var index = _jobs.FindIndex(j => j.Status == JobStatus.Pending);
            if (index < 0) return null;

            var original = Clone(_jobs[index]);
            try
            {
                _jobs[index].MarkProcessing(settings.PrinterName, settings.Revision);
                Save();
                return Clone(_jobs[index]);
            }
            catch
            {
                _jobs[index] = original;
                throw;
            }
        }
    }

    public PrintJob? TakeNextPending(string printerName, long settingsRevision)
    {
        lock (_gate)
        {
            var index = _jobs.FindIndex(j => j.Status == JobStatus.Pending);
            if (index < 0) return null;

            var original = Clone(_jobs[index]);
            try
            {
                _jobs[index].MarkProcessing(printerName, settingsRevision);
                Save();
                return Clone(_jobs[index]);
            }
            catch
            {
                _jobs[index] = original;
                throw;
            }
        }
    }

    public void AppendStep(Guid id, string stage, string where, string message, string? detail = null, bool isError = false)
    {
        lock (_gate)
        {
            var job = _jobs.First(j => j.Id == id);
            job.AddStep(stage, where, message, detail, isError);
            Save();
        }
    }

    public void Finish(Guid id, JobStatus status, string? error = null, string? errorReason = null, string? errorWhere = null)
    {
        lock (_gate)
        {
            var job = _jobs.First(j => j.Id == id);
            job.MarkFinished(status, error, errorReason, errorWhere);
            Save();
        }
    }

    public int PurgeOlderThan(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            var removable = _jobs
                .Where(j => j.Status is not (JobStatus.Pending or JobStatus.Processing))
                .Where(j => (j.FinishedAt ?? j.CreatedAt) < cutoff)
                .Select(j => j.Id)
                .ToHashSet();
            if (removable.Count == 0) return 0;
            _jobs.RemoveAll(j => removable.Contains(j.Id));
            Save();
            return removable.Count;
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        var payload = _jobs.Select(ToRecord).ToList();
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static PrintJob Clone(PrintJob job) => new(
        job.Id, job.Reference, job.Text, job.Status, job.CreatedAt,
        job.FinishedAt, job.Error, job.PrinterName, job.SettingsRevision,
        job.Steps.ToArray(), job.ErrorReason, job.ErrorWhere,
        job.JobType, job.ContentKind, job.SourcePath, job.TemplateName, job.ReprintedFromId);

    private static PrintJob ToDomain(JobRecord record) => new(
        record.Id, record.Reference, record.Text, JobStatusExtensions.FromWire(record.Status),
        record.CreatedAt, record.FinishedAt, record.Error, record.PrinterName, record.SettingsRevision,
        record.Steps?.Select(s => new ProcessingStep(s.At, s.Stage, s.Where, s.Message, s.Detail, s.IsError)),
        record.ErrorReason, record.ErrorWhere,
        record.JobType ?? "default",
        JobContentKindExtensions.FromWire(record.ContentKind),
        record.SourcePath, record.TemplateName, record.ReprintedFromId);

    private static JobRecord ToRecord(PrintJob job) => new(
        job.Id, job.Reference, job.Text, job.Status.ToWire(), job.CreatedAt,
        job.FinishedAt, job.Error, job.PrinterName, job.SettingsRevision,
        job.Steps.Select(s => new StepRecord(s.At, s.Stage, s.Where, s.Message, s.Detail, s.IsError)).ToList(),
        job.ErrorReason, job.ErrorWhere, job.JobType, job.ContentKind.ToWire(),
        job.SourcePath, job.TemplateName, job.ReprintedFromId);

    private sealed record StepRecord(
        DateTimeOffset At, string Stage, string Where, string Message,
        string? Detail = null, bool IsError = false);

    private sealed record JobRecord(
        Guid Id, string Reference, string Text, string Status, DateTimeOffset CreatedAt,
        DateTimeOffset? FinishedAt = null, string? Error = null, string? PrinterName = null,
        long? SettingsRevision = null, List<StepRecord>? Steps = null,
        string? ErrorReason = null, string? ErrorWhere = null,
        string? JobType = null, string? ContentKind = null,
        string? SourcePath = null, string? TemplateName = null, Guid? ReprintedFromId = null);
}
