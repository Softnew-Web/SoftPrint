using AutoPrint.Application.Abstractions;
using AutoPrint.Domain;
using AutoPrint.UI;

namespace AutoPrint.Application.Services;

/// <summary>Consulta, abre e escolhe a pasta de entrada configurada.</summary>
public sealed class InboxService(ISettingsRepository settings, IJobRepository jobs)
{
    public object Snapshot()
    {
        var options = settings.Current;
        var folder = options.InboxFolder?.Trim() ?? "";
        var exists = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        var files = exists
            ? Directory.GetFiles(folder)
                .Where(f => InboxFileRules.TryGetContentKind(f, out _))
                .Select(ToSnapshot)
                .OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        return new
        {
            folder,
            enabled = options.InboxEnabled,
            deleteAfterPrint = options.DeleteInboxAfterPrint,
            exists,
            fileCount = files.Length,
            files
        };
    }

    private InboxFileSnapshot ToSnapshot(string path)
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

    public string OpenFolder()
    {
        var folder = settings.Current.InboxFolder?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Configure a pasta de entrada antes de abrir.");
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
        return folder;
    }

    /// <summary>Abre o seletor nativo do Windows na thread da UI (acima do WebView2).</summary>
    public string? BrowseFolder()
    {
        var start = settings.Current.InboxFolder?.Trim() ?? "";
        return UiHost.Invoke(() => ShowFolderDialog(start));
    }

    private static string? ShowFolderDialog(string startPath)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha a pasta onde o sistema web grava os arquivos para impressão",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            AutoUpgradeEnabled = true
        };
        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
            dialog.SelectedPath = startPath;

        var owner = UiHost.MainForm;
        var result = owner is { IsHandleCreated: true, IsDisposed: false }
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();

        return result == DialogResult.OK ? dialog.SelectedPath : null;
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
