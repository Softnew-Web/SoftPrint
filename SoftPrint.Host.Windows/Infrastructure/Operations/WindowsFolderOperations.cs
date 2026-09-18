using SoftPrint.Application.Abstractions;
using SoftPrint.UI;

namespace SoftPrint.Infrastructure.Operations;

public sealed class WindowsFolderOperations : IFolderOperations
{
    public bool CanBrowse => true;

    public string Open(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
        return path;
    }

    public string? Browse(string startPath) =>
        UiHost.Invoke(() =>
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
        });
}
