namespace SoftPrint.Application.Abstractions;

/// <summary>Histórico leve da última verificação / instalação de update.</summary>
public interface IUpdateHistoryStore
{
    UpdateHistorySnapshot Get();
    void RecordCheck(UpdateCheckResult result);
    void RecordApplyStarted(string version, bool rollback);
    void RecordApplyFailed(string version, bool rollback, string error);
    void RecordApplySucceeded(string version, bool rollback);
}

public sealed record UpdateHistorySnapshot(
    DateTimeOffset? LastCheckedAt,
    string? LastCheckError,
    string? LastLatestVersion,
    bool? LastUpdateAvailable,
    DateTimeOffset? LastApplyAt,
    string? LastApplyVersion,
    bool? LastApplyOk,
    string? LastApplyError,
    string? LastApplyKind);
