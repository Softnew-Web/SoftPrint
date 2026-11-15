using System.Text.Json;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Updates;

namespace SoftPrint.Infrastructure.Operations;

/// <summary>
/// Confirma sucesso de update/rollback só na próxima subida (após o script de instalação).
/// </summary>
public static class PendingUpdateFinalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// No estado local: o processo que dispara o update e o reaberto pelo script calculam o mesmo
    /// lugar (a raiz do --config-root chega ao reaberto pelo ambiente herdado).
    /// </summary>
    public static string MarkerPath => Path.Combine(
        SoftPrintInstallLocation.LocalStateRoot,
        "pending-update.json");

    public static void MarkPending(string version, bool rollback)
    {
        try
        {
            var dir = Path.GetDirectoryName(MarkerPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            var payload = new PendingMarker(version, rollback, DateTimeOffset.UtcNow, SoftPrintVersion.Current);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch
        {
            /* ignore */
        }
    }

    public static void TryFinalize(IUpdateHistoryStore history)
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;
            var json = File.ReadAllText(MarkerPath);
            var pending = JsonSerializer.Deserialize<PendingMarker>(json, JsonOptions);
            if (pending is null)
            {
                File.Delete(MarkerPath);
                return;
            }

            var current = SoftPrintVersionCompare.Normalize(SoftPrintVersion.Current);
            var target = SoftPrintVersionCompare.Normalize(pending.TargetVersion);
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                history.RecordApplySucceeded(pending.TargetVersion, pending.Rollback);
            else
            {
                history.RecordApplyFailed(
                    pending.TargetVersion,
                    pending.Rollback,
                    $"Após reinício a versão é {SoftPrintVersion.Current}, esperado {pending.TargetVersion}.");
            }

            File.Delete(MarkerPath);
        }
        catch
        {
            /* ignore */
        }
    }

    private sealed record PendingMarker(
        string TargetVersion,
        bool Rollback,
        DateTimeOffset MarkedAt,
        string FromVersion);
}
