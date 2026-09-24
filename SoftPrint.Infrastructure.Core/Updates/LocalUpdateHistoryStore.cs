using System.Text.Json;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Updates;

public sealed class LocalUpdateHistoryStore : IUpdateHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private UpdateHistorySnapshot _current;

    public LocalUpdateHistoryStore()
    {
        _current = Load() ?? new UpdateHistorySnapshot(
            null, null, null, null, null, null, null, null, null);
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftPrint",
        "update-history.json");

    public UpdateHistorySnapshot Get()
    {
        lock (_gate) return _current;
    }

    public void RecordCheck(UpdateCheckResult result)
    {
        lock (_gate)
        {
            _current = _current with
            {
                LastCheckedAt = result.CheckedAt,
                LastCheckError = result.Error,
                LastLatestVersion = result.LatestVersion,
                LastUpdateAvailable = result.Error is null ? result.UpdateAvailable : null
            };
            Save(_current);
        }
    }

    public void RecordApplyStarted(string version, bool rollback) =>
        RecordApply(version, rollback, ok: null, error: null);

    public void RecordApplyFailed(string version, bool rollback, string error) =>
        RecordApply(version, rollback, ok: false, error: error);

    public void RecordApplySucceeded(string version, bool rollback) =>
        RecordApply(version, rollback, ok: true, error: null);

    private void RecordApply(string version, bool rollback, bool? ok, string? error)
    {
        lock (_gate)
        {
            _current = _current with
            {
                LastApplyAt = DateTimeOffset.Now,
                LastApplyVersion = version,
                LastApplyOk = ok,
                LastApplyError = error,
                LastApplyKind = rollback ? "rollback" : "update"
            };
            Save(_current);
        }
    }

    private static UpdateHistorySnapshot? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<UpdateHistorySnapshot>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void Save(UpdateHistorySnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
            /* ignore */
        }
    }
}
