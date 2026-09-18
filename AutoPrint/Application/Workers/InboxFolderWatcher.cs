using AutoPrint.Application.Abstractions;
using AutoPrint.Application.Services;
using AutoPrint.Domain;

namespace AutoPrint.Application.Workers;

/// <summary>Vigia a pasta de entrada e enfileira PDF/imagens novos.</summary>
public sealed class InboxFolderWatcher(
    JobQueueService jobs,
    ISettingsRepository settings,
    ILogger<InboxFolderWatcher> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTime> _blockedUntil = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ScanOnce();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao varrer pasta de entrada.");
            }

            await Task.Delay(1500, stoppingToken);
        }
    }

    private void ScanOnce()
    {
        var options = settings.Current;
        if (!options.InboxEnabled || string.IsNullOrWhiteSpace(options.InboxFolder))
            return;

        if (!Directory.Exists(options.InboxFolder))
        {
            try { Directory.CreateDirectory(options.InboxFolder); }
            catch { return; }
        }

        var now = DateTime.UtcNow;
        foreach (var key in _blockedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            _blockedUntil.Remove(key);

        string[] files;
        try
        {
            files = Directory.GetFiles(options.InboxFolder);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Não foi possível listar a pasta de entrada.");
            return;
        }

        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!InboxFileRules.TryGetContentKind(file, out var kind))
                continue;

            var full = Path.GetFullPath(file);
            if (_blockedUntil.ContainsKey(full))
                continue;

            if (IsAlreadyQueued(full))
                continue;

            if (!InboxFileRules.IsFileReady(full))
                continue;

            // Evita reenfileirar o mesmo arquivo após falha até ele mudar.
            if (HasStaleFailure(full))
                continue;

            try
            {
                var job = jobs.Submit(
                    InboxFileRules.BuildReference(full),
                    text: Path.GetFileName(full),
                    jobType: "inbox",
                    contentKind: kind.ToWire(),
                    sourcePath: full);
                logger.LogInformation("Pasta de entrada: enfileirado {File} como {JobId}", full, job.Id);
            }
            catch (ArgumentException ex)
            {
                logger.LogDebug(ex, "Ignorado {File}", full);
                _blockedUntil[full] = now.AddSeconds(30);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao enfileirar {File}", full);
                _blockedUntil[full] = now.AddSeconds(15);
            }
        }
    }

    private bool IsAlreadyQueued(string fullPath) =>
        jobs.List().Any(j =>
            (j.Status is JobStatus.Pending or JobStatus.Processing)
            && string.Equals(j.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));

    private bool HasStaleFailure(string fullPath)
    {
        var failed = jobs.List()
            .Where(j => j.Status == JobStatus.Uncertain
                        && string.Equals(j.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault();
        if (failed is null) return false;
        try
        {
            // Só bloqueia se o arquivo não foi reescrito depois da falha.
            return File.GetLastWriteTimeUtc(fullPath) <= failed.CreatedAt.UtcDateTime;
        }
        catch
        {
            return true;
        }
    }
}
