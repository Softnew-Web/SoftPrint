using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;

namespace SoftPrint.Application.Workers;

/// <summary>Vigia todas as pastas de entrada configuradas e enfileira PDF/imagens novos.</summary>
public sealed class InboxFolderWatcher(
    JobQueueService jobs,
    ISettingsRepository settings,
    IRecentErrorLog errorLog,
    ILogger<InboxFolderWatcher> logger) : BackgroundService
{
    // Chave: caminho absoluto do arquivo → bloqueado até quando.
    private readonly Dictionary<string, DateTime> _blockedUntil = new(StringComparer.OrdinalIgnoreCase);
    // Caminho → data de alteração do arquivo quando foi enfileirado. Cobre relógio adiantado
    // (compartilhamento de rede) e pedidos já apagados pela retenção enquanto o app está aberto.
    private readonly Dictionary<string, DateTime> _submittedVersion = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ScanAll();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao varrer pastas de entrada.");
            }

            await Task.Delay(1500, stoppingToken);
        }
    }

    private void ScanAll()
    {
        var now = DateTime.UtcNow;

        // Remove entradas de bloqueio expiradas.
        foreach (var key in _blockedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            _blockedUntil.Remove(key);
        foreach (var key in _submittedVersion.Keys.Where(k => !File.Exists(k)).ToList())
            _submittedVersion.Remove(key);

        var entries = settings.Current.InboxEntries;
        foreach (var entry in entries)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Folder))
                continue;

            ScanEntry(entry, now);
        }
    }

    private void ScanEntry(InboxEntry entry, DateTime now)
    {
        if (!Directory.Exists(entry.Folder))
        {
            try { Directory.CreateDirectory(entry.Folder); }
            catch { return; }
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(entry.Folder);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Não foi possível listar a pasta de entrada '{Folder}'.", entry.Folder);
            errorLog.Record("pasta-entrada", $"Pasta '{entry.Label}' inacessível: {ex.Message}");
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

            if (!TryGetChangedAtUtc(full, out var changedAt) || WasAlreadyProcessed(full, changedAt))
                continue;

            try
            {
                var printerOverride = string.IsNullOrWhiteSpace(entry.PrinterName) ? null : entry.PrinterName;
                var job = jobs.Submit(
                    InboxFileRules.BuildReference(full),
                    text: Path.GetFileName(full),
                    jobType: "inbox",
                    contentKind: kind.ToWire(),
                    sourcePath: full,
                    requestedPrinterName: printerOverride,
                    settingsOverride: entry.CustomSettings);
                _submittedVersion[full] = changedAt;
                logger.LogInformation(
                    "Pasta de entrada '{Label}': enfileirado {File} como {JobId}",
                    entry.Label, full, job.Id);
            }
            catch (ArgumentException ex)
            {
                logger.LogDebug(ex, "Ignorado {File}", full);
                _blockedUntil[full] = now.AddSeconds(30);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao enfileirar {File}", full);
                errorLog.Record("pasta-entrada", $"{Path.GetFileName(full)}: {ex.Message}");
                _blockedUntil[full] = now.AddSeconds(15);
            }
        }
    }

    private bool IsAlreadyQueued(string fullPath) =>
        jobs.List().Any(j =>
            (j.Status is JobStatus.Pending or JobStatus.Processing)
            && string.Equals(j.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// O arquivo já gerou um pedido (impresso, simulado ou com falha) e não mudou desde então.
    /// Sem isso, quem não marca "Apagar após imprimir" recebe uma impressão nova a cada varredura.
    /// </summary>
    private bool WasAlreadyProcessed(string fullPath, DateTime changedAtUtc)
    {
        if (_submittedVersion.TryGetValue(fullPath, out var submitted) && submitted == changedAtUtc)
            return true;

        var last = jobs.List()
            .Where(j => j.Status is JobStatus.Sent or JobStatus.Simulated or JobStatus.Uncertain
                        && string.Equals(j.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault();
        return last is not null && changedAtUtc <= last.CreatedAt.UtcDateTime;
    }

    /// <summary>
    /// Copiar um arquivo preserva a data de modificação, mas a de criação é a da cópia:
    /// o mesmo arquivo colocado de novo na pasta conta como novo.
    /// </summary>
    private static bool TryGetChangedAtUtc(string path, out DateTime changedAtUtc)
    {
        try
        {
            var info = new FileInfo(path);
            changedAtUtc = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
            return true;
        }
        catch
        {
            changedAtUtc = default;
            return false;
        }
    }
}
