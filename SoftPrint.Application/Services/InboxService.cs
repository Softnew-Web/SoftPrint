using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Application.Services;

/// <summary>Consulta, abre e gerencia as pastas de entrada configuradas.</summary>
public sealed class InboxService(
    ISettingsRepository settings,
    IJobRepository jobs,
    IFolderOperations folders)
{
    /// <summary>Retorna snapshot de todas as entradas configuradas.</summary>
    public object Snapshot()
    {
        var entries = settings.Current.InboxEntries;
        return entries.Select(e => EntrySnapshot(e)).ToArray();
    }

    /// <summary>Retorna snapshot de uma entrada específica.</summary>
    public object EntrySnapshot(Guid id)
    {
        var entry = FindEntry(id);
        return EntrySnapshot(entry);
    }

    public string OpenFolder(Guid id)
    {
        var entry = FindEntry(id);
        var folder = entry.Folder.Trim();
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Configure a pasta desta entrada antes de abrir.");
        Directory.CreateDirectory(folder);
        return folders.Open(folder);
    }

    public string? BrowseFolder(Guid id)
    {
        if (!folders.CanBrowse) return null;
        var entry = FindEntry(id);
        return folders.Browse(entry.Folder.Trim());
    }

    public string? BrowseFolderFree(string startPath)
    {
        if (!folders.CanBrowse) return null;
        return folders.Browse(startPath?.Trim() ?? "");
    }

    private InboxEntry FindEntry(Guid id)
    {
        var entry = settings.Current.InboxEntries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            throw new ArgumentException($"Entrada de inbox {id} não encontrada.");
        return entry;
    }

    private object EntrySnapshot(InboxEntry entry)
    {
        var folder = entry.Folder.Trim();
        var exists = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        var files = exists
            ? Directory.GetFiles(folder)
                .Where(f => InboxFileRules.TryGetContentKind(f, out _))
                .Select(ToFileSnapshot)
                .OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        return new
        {
            id = entry.Id,
            label = entry.Label,
            printerName = entry.PrinterName,
            folder,
            enabled = entry.Enabled,
            deleteAfterPrint = entry.DeleteAfterPrint,
            exists,
            fileCount = files.Length,
            files,
            hasCustomSettings = entry.CustomSettings is not null,
            imageFit = entry.CustomSettings?.ImageFit.ToWire(),
            imageScalePercent = entry.CustomSettings?.ImageScalePercent,
            paperSize = entry.CustomSettings?.PaperSize.ToWire(),
            paperWidthMm = entry.CustomSettings?.PaperWidthMm,
            paperHeightMm = entry.CustomSettings?.PaperHeightMm,
            paperLandscape = entry.CustomSettings?.PaperLandscape,
        };
    }

    private InboxFileSnapshot ToFileSnapshot(string path)
    {
        var full = Path.GetFullPath(path);
        var latest = jobs.Snapshot()
            .Where(j => string.Equals(j.SourcePath, full, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault();
        var ready = InboxFileRules.IsFileReady(full);
        var status = latest?.Status switch
        {
            JobStatus.Pending => "queued",
            JobStatus.Processing => "printing",
            JobStatus.Sent => "sent",
            JobStatus.Simulated => "simulated",
            JobStatus.Uncertain => "failed",
            _ => ready ? "waiting" : "copying"
        };

        return new InboxFileSnapshot(
            Path.GetFileName(full), full, SafeLength(full),
            InboxFileRules.TryGetContentKind(full, out var k) ? k.ToWire() : "unknown",
            File.GetLastWriteTime(full), status, latest?.Id, latest?.Error, latest?.ErrorReason);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private sealed record InboxFileSnapshot(
        string name,
        string path,
        long size,
        string kind,
        DateTime modifiedAt,
        string status,
        Guid? jobId,
        string? error,
        string? errorReason);
}
