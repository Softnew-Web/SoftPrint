using AutoPrint.Application.Abstractions;
using AutoPrint.Domain;

namespace AutoPrint.Infrastructure.Integrations;

public sealed class TrayAppNotifier : IAppNotifier
{
    private NotifyIcon? _icon;

    public void Attach(NotifyIcon icon) => _icon = icon;

    public void NotifyUncertain(PrintJob job)
    {
        try
        {
            _icon?.ShowBalloonTip(
                8000,
                "AutoPrint — conferir envio",
                $"{job.Reference}: {job.ErrorReason ?? job.Error ?? "Falha no envio"}",
                ToolTipIcon.Warning);
        }
        catch { /* ignore tray failures */ }
    }

    public void NotifyCompleted(PrintJob job)
    {
        try
        {
            if (job.Status == JobStatus.Uncertain) return;
            _icon?.ShowBalloonTip(
                4000,
                "AutoPrint — concluído",
                $"{job.Reference}: {job.Status.ToDisplay()}",
                ToolTipIcon.Info);
        }
        catch { /* ignore */ }
    }
}

public sealed class WindowsStartupService(IHostEnvironment environment) : IWindowsStartupService
{
    private const string ValueName = "AutoPrint";
    private static string RunKey => @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void ApplyFromOptions(bool enable)
    {
        var exe = Path.Combine(environment.ContentRootPath, "AutoPrint.exe");
        if (!File.Exists(exe))
            exe = Environment.ProcessPath ?? exe;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true)
                        ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enable)
            key.SetValue(ValueName, $"\"{exe}\"");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, false);
    }
}
