using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SoftPrint.Host.Windows.Legacy
{
    internal static class LegacyBackgroundHost
    {
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        public static void EnsureConsole() => AllocConsole();

        public static void StartTray(IHostApplicationLifetime lifetime, IServiceProvider services, string address, bool startHidden)
        {
            lifetime.ApplicationStarted.Register(() =>
            {
                var thread = new Thread(() =>
                {
#if NET5_0_OR_GREATER
                    System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
                    System.Windows.Forms.Application.EnableVisualStyles();
                    System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                    using (var notify = new NotifyIcon
                    {
                        Visible = true,
                        Text = "SoftPrint Legacy — em segundo plano",
                        Icon = SystemIcons.Application,
                        BalloonTipTitle = "SoftPrint"
                    })
                    using (var menu = new ContextMenuStrip())
                    {
                        menu.Items.Add("Abrir painel", null, (sender1, e1) => OpenDashboard(address));
                        menu.Items.Add("Sair", null, (sender2, e2) => lifetime.StopApplication());
                        notify.ContextMenuStrip = menu;
                        notify.DoubleClick += (sender3, e3) => OpenDashboard(address);
                        notify.MouseClick += (s, e) =>
                        {
                            if (e.Button == MouseButtons.Left)
                                OpenDashboard(address);
                        };
                        lifetime.ApplicationStopping.Register(() =>
                        {
                            try
                            {
                                notify.Visible = false;
                                System.Windows.Forms.Application.Exit();
                            }
                            catch { /* ignore */ }
                        });
                        if (!startHidden)
                        {
                            OpenDashboard(address);
                        }
                        else
                        {
                            var sysRepo = services.GetService<SoftPrint.Application.Abstractions.ISystemSettingsRepository>();
                            var alerts = sysRepo?.Current.SoundEnabled != false;
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
                    }
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
}
