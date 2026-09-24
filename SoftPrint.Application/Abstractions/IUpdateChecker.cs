namespace SoftPrint.Application.Abstractions;

public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Retorna o último resultado em cache, sem chamar a rede.</summary>
    UpdateCheckResult? TryGetCached();

    void InvalidateCache();
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    bool Mandatory,
    string? DownloadUrl,
    string? ReleaseUrl,
    string? ReleaseNotes,
    string? Error,
    DateTimeOffset CheckedAt,
    string? AssetName = null,
    string? Sha256 = null);
