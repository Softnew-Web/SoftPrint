using Microsoft.Win32;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Host.Windows.Legacy;

public sealed class LegacyFolderOperations : IFolderOperations
{
    public bool CanBrowse => false;
    public string? Browse(string startPath) => null;
    public string Open(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
        return path;
    }
}

public sealed class LegacyStartupService : IWindowsStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue("SoftPrint") is string;
        }
    }

    public void ApplyFromOptions(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enable)
            key.SetValue("SoftPrint", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("SoftPrint", false);
        key.DeleteValue("AutoPrint", false);
    }
}

public sealed class LegacyNoOpNotifier : IAppNotifier
{
    public void NotifyCompleted(PrintJob job) { }
    public void NotifyUncertain(PrintJob job) { }
}
