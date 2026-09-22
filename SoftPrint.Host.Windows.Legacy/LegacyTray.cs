using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoftPrint.Host.Windows.Legacy;

internal static class LegacyBackgroundHost
{
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    public static void EnsureConsole() => AllocConsole();

    public static void StartTray(WebApplication app, bool startHidden)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = (app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5178").TrimEnd('/');
            var thread = new Thread(() =>
            {
                System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                using var notify = new NotifyIcon
                {
                    Visible = true,
                    Text = "SoftPrint Legacy — em segundo plano",
                    Icon = SystemIcons.Application,
                    BalloonTipTitle = "SoftPrint"
                };
                using var menu = new ContextMenuStrip();
                menu.Items.Add("Abrir painel", null, (_, _) => OpenDashboard(address));
                menu.Items.Add("Sair", null, (_, _) => app.Lifetime.StopApplication());
                notify.ContextMenuStrip = menu;
                notify.DoubleClick += (_, _) => OpenDashboard(address);
                notify.MouseClick += (_, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                        OpenDashboard(address);
                };
                app.Lifetime.ApplicationStopping.Register(() =>
                {
                    try { notify.Visible = false; System.Windows.Forms.Application.Exit(); }
                    catch { /* ignore */ }
                });
                if (!startHidden)
                    OpenDashboard(address);
                else
                {
                    var alerts = app.Services.GetService<SoftPrint.Application.Abstractions.ISystemSettingsRepository>()
                        ?.Current.SoundEnabled != false;
                    if (alerts)
                    {
                        notify.ShowBalloonTip(
                            4000,
                            "SoftPrint",
                            "Impressão em segundo plano. Clique no ícone da bandeja para abrir o painel.",
                            ToolTipIcon.Info);
                    }
                }
                System.Windows.Forms.Application.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        });
    }

    private static void OpenDashboard(string address)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"{address}/dashboard") { UseShellExecute = true });
        }
        catch
        {
            /* ignore */
        }
    }
}
