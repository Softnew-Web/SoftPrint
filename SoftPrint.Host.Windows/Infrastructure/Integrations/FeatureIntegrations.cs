using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Infrastructure.Integrations;

public sealed class TrayAppNotifier(ISystemSettingsRepository settings) : IAppNotifier
{
    private NotifyIcon? _icon;

    public void Attach(NotifyIcon icon) => _icon = icon;

    /// <summary>Balão da bandeja; respeita "Notificações na bandeja" em tempo real.</summary>
    public void ShowBalloonTip(int timeoutMs, string title, string text, ToolTipIcon icon)
    {
        if (!settings.Current.SoundEnabled) return;
        try
        {
            _icon?.ShowBalloonTip(timeoutMs, title, text, icon);
        }
        catch { /* ignore tray failures */ }
    }

    public void NotifyUncertain(PrintJob job)
    {
        ShowBalloonTip(
            8000,
            "SoftPrint — falha na impressão",
            $"{job.Reference}: {job.ErrorReason ?? job.Error ?? "Falha no envio"}",
            ToolTipIcon.Warning);
    }

    public void NotifyCompleted(PrintJob job)
    {
        // Sucesso silencioso — só falhas e updates sobem na bandeja.
    }

    public void NotifyUpdateAvailable(string currentVersion, string latestVersion, bool mandatory)
    {
        ShowBalloonTip(
            mandatory ? 12_000 : 8_000,
            mandatory ? "SoftPrint — atualização obrigatória" : "SoftPrint — nova versão",
            $"v{currentVersion} → v{latestVersion}",
            mandatory ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public void NotifyUpdateFailed(string message)
    {
        ShowBalloonTip(
            10_000,
            "SoftPrint — falha na atualização",
            Truncate(message, 180),
            ToolTipIcon.Error);
    }

    public void NotifyQueueAlert(string title, string message)
    {
        ShowBalloonTip(10_000, title, Truncate(message, 180), ToolTipIcon.Warning);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}

public sealed class WindowsStartupService(IHostEnvironment environment) : IWindowsStartupService
{
    private const string ValueName = "SoftPrint";
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
        var exe = Path.Combine(environment.ContentRootPath, "SoftPrint.exe");
        if (!File.Exists(exe))
            exe = Environment.ProcessPath ?? exe;

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true)
                        ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enable)
            key.SetValue(ValueName, $"\"{exe}\" --tray");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, false);
        if (key.GetValue("AutoPrint") is not null)
            key.DeleteValue("AutoPrint", false);
    }
}
