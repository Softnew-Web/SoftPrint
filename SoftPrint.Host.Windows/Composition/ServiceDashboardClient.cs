using System.Security.Cryptography;
using System.Text;
using SoftPrint.Application;
using SoftPrint.Hosting;
using SoftPrint.UI;

namespace SoftPrint.Composition;

/// <summary>
/// Painel da sessão do usuário quando o motor já roda como serviço.
/// Fechar esta janela não encerra a fila.
/// </summary>
internal static class ServiceDashboardClient
{
    public static int Run(bool startInTray)
    {
        using var instance = UiInstance.TryAcquire();
        if (instance is null)
        {
            MessageBox.Show(
                "O painel do SoftPrint já está aberto.\n\n" +
                "Se não aparecer, procure o ícone do SoftPrint perto do relógio.",
                "SoftPrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        WindowsServiceControl.WaitForListenPort();

        var code = 0;
        var thread = new Thread(() => code = RunSta(startInTray, instance.ShowRequested))
        {
            IsBackground = false,
            Name = "SoftPrint.Panel"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return code;
    }

    private static int RunSta(bool startInTray, EventWaitHandle showSignal)
    {
        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        }
        catch (InvalidOperationException)
        {
            /* estilo já definido */
        }

        var address = WindowsServiceControl.ResolveDashboardAddress();
        using var notify = new NotifyIcon
        {
            Visible = true,
            Text = "SoftPrint (serviço do Windows)",
            Icon = SystemIcons.Application
        };

        var panel = CreatePanel(address, notify);
        notify.DoubleClick += (_, _) => Show(panel);
        notify.MouseDoubleClick += (_, _) => Show(panel);

        var wait = ThreadPool.RegisterWaitForSingleObject(
            showSignal,
            (_, _) => Show(panel),
            null,
            Timeout.Infinite,
            false);

        if (startInTray)
        {
            panel.ShowInTaskbar = false;
            panel.Shown += (_, _) => panel.HideToTray(balloon: false);
        }

        var balloonShown = false;
        panel.VisibleChanged += (_, _) =>
        {
            if (panel.Visible || balloonShown || panel.IsDisposed)
                return;
            balloonShown = true;
            try
            {
                notify.ShowBalloonTip(
                    5000,
                    "SoftPrint",
                    "A impressão continua no serviço do Windows, mesmo depois do logoff.",
                    ToolTipIcon.Info);
            }
            catch
            {
                /* bandeja indisponível */
            }
        };

        try
        {
            System.Windows.Forms.Application.Run(panel);
        }
        finally
        {
            wait.Unregister(null);
            notify.Visible = false;
            try { panel.Dispose(); } catch { /* ignore */ }
        }

        return 0;
    }

    private static Dashboard CreatePanel(string address, NotifyIcon notify)
    {
        var panel = new Dashboard(address, "", new SoftPrintFeatureOptions());
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir painel", null, (_, _) => Show(panel));
        var info = menu.Items.Add("Impressão ativa no serviço do Windows");
        info.Enabled = false;
        menu.Items.Add("Fechar painel", null, (_, _) => panel.RequestExit());
        notify.ContextMenuStrip = menu;
        return panel;
    }

    private static void Show(Dashboard panel)
    {
        try
        {
            if (panel.IsDisposed)
                return;
            if (panel.InvokeRequired)
            {
                panel.BeginInvoke(() => Show(panel));
                return;
            }

            panel.ShowFromTray();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            /* painel já fechou */
        }
    }

    private sealed class UiInstance : IDisposable
    {
        private readonly Mutex _mutex;
        public EventWaitHandle ShowRequested { get; }

        private UiInstance(Mutex mutex, EventWaitHandle showRequested)
        {
            _mutex = mutex;
            ShowRequested = showRequested;
        }

        public static UiInstance? TryAcquire()
        {
            var suffix = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..24];
            var mutex = new Mutex(true, "Local\\SoftPrint-Ui-" + suffix, out var acquired);
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SoftPrint-Ui-Show-" + suffix);
            if (acquired)
                return new UiInstance(mutex, show);

            try { show.Set(); }
            finally
            {
                mutex.Dispose();
                show.Close();
            }

            return null;
        }

        public void Dispose()
        {
            _mutex.Dispose();
            ShowRequested.Dispose();
        }
    }
}
