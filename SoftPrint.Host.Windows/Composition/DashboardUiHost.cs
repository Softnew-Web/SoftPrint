using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.UI;

namespace SoftPrint.Composition;

/// <summary>Sessão WinForms do painel (splash, tray, loop do Dashboard).</summary>
internal sealed class DashboardUiHost
{
    private const string AppTitle = "SoftPrint";

    private readonly WebApplication _app;
    private readonly string _address;
    private readonly string _apiKey;
    private readonly SoftPrintFeatureOptions _features;
    private readonly TrayAppNotifier _tray;
    private readonly bool _startInTray;
    private readonly EventWaitHandle? _showSignal;

    private bool _stopHostOnClose;
    private Dashboard? _livePanel;
    private NotifyIcon? _notify;
    private IAppLifecycleLogger? _lifecycle;

    public DashboardUiHost(
        WebApplication app,
        string address,
        string apiKey,
        SoftPrintFeatureOptions features,
        TrayAppNotifier tray,
        bool startInTray,
        EventWaitHandle? showSignal)
    {
        _app = app;
        _address = address;
        _apiKey = apiKey;
        _features = features;
        _tray = tray;
        _startInTray = startInTray;
        _showSignal = showSignal;
        try { _lifecycle = app.Services.GetService<IAppLifecycleLogger>(); }
        catch { /* ignore */ }
    }

    public void Run()
    {
        PrepareWinForms();
        HookWindowsSessionEvents();
        ShowPendingUpdateFailure();

        SplashForm? splash = null;
        IUpdateApplier? updateApplier = null;
        Task? startupUpdateTask = null;

        if (!_startInTray)
            (splash, updateApplier, startupUpdateTask) = BeginSplashAndUpdateCheck();

        using var notify = CreateNotifyIcon();
        _notify = notify;
        _tray.Attach(notify);

        var panel = CreatePanel();
        notify.DoubleClick += (_, _) => BringLiveToFront();
        notify.MouseDoubleClick += (_, _) => BringLiveToFront();

        var skipDashboard = FinishSplash(splash, updateApplier, startupUpdateTask);
        splash = null;

        using var registration = _app.Lifetime.ApplicationStopping.Register(OnApplicationStopping);
        var wait = RegisterShowSignal();
        ConfigureInitialPanelVisibility(panel);

        if (skipDashboard && ShouldAbortForRestart(updateApplier, wait))
            return;

        try
        {
            RunPanelLoop(ref panel);
        }
        catch (Exception ex)
        {
            try { _lifecycle?.OnCrash(ex, "Erro no painel SoftPrint"); }
            catch { /* ignore */ }
            if (!IsBenignShutdownNoise(ex))
                TryShowError("O SoftPrint encerrou após o carregamento.\n\n" + ex.Message);
        }
        finally
        {
            UnhookWindowsSessionEvents();
            wait?.Unregister(null);
            try { if (_notify is not null) _notify.Visible = false; } catch { /* ignore */ }
            try { _livePanel?.Dispose(); } catch { /* ignore */ }
            _app.Lifetime.StopApplication();
        }
    }

    private void PrepareWinForms()
    {
        try
        {
            // Tem de vir ANTES de qualquer Form (splash incluso).
            System.Windows.Forms.Application.SetUnhandledExceptionMode(
                UnhandledExceptionMode.CatchException);
        }
        catch (InvalidOperationException)
        {
            /* já definido — seguir */
        }

        System.Windows.Forms.Application.ThreadException += OnUiThreadException;

        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
    }

    private void OnUiThreadException(object? sender, ThreadExceptionEventArgs args)
    {
        try { _lifecycle?.OnCrash(args.Exception, "Exceção na UI (ThreadException)"); }
        catch { /* ignore */ }

        // Ruído típico no encerramento: painel ainda vivo, host DI já descartado.
        if (IsBenignShutdownNoise(args.Exception))
            return;

        TryShowError("Erro no painel SoftPrint:\n\n" + args.Exception.Message);
    }

    private static bool IsBenignShutdownNoise(Exception ex)
    {
        if (ex is not ObjectDisposedException ode)
            return false;
        var name = ode.ObjectName ?? "";
        return name.Contains("IServiceProvider", StringComparison.OrdinalIgnoreCase)
               || name.Contains("ServiceProvider", StringComparison.OrdinalIgnoreCase);
    }

    private void ShowPendingUpdateFailure()
    {
        try
        {
            var failMsg = SoftPrintHostExtensions.TryConsumeUpdateFailureMessage(_app.Services);
            if (string.IsNullOrWhiteSpace(failMsg))
                return;

            MessageBox.Show(
                failMsg,
                AppTitle + " — falha na atualização",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch
        {
            /* ignore */
        }
    }

    private (SplashForm splash, IUpdateApplier? applier, Task? task) BeginSplashAndUpdateCheck()
    {
        var splash = new SplashForm(durationMs: 15_000);
        splash.Show();
        splash.StartProgress();
        System.Windows.Forms.Application.DoEvents();

        IUpdateApplier? applier = null;
        Task? task = null;
        if (_features.UpdateCheckEnabled)
        {
            var checker = _app.Services.GetRequiredService<IUpdateChecker>();
            applier = _app.Services.GetRequiredService<IUpdateApplier>();
            task = ApplicationComposer.RunStartupUpdateCheck(
                splash, checker, applier, _features.AutoUpdateOnStartup);
        }

        return (splash, applier, task);
    }

    private NotifyIcon CreateNotifyIcon() => new()
    {
        Visible = true,
        Text = $"SoftPrint v{SoftPrint.Domain.SoftPrintVersion.Current}",
        Icon = SystemIcons.Application,
        BalloonTipTitle = AppTitle
    };

    private Dashboard CreatePanel()
    {
        var p = new Dashboard(_address, _apiKey, _features, _tray);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir painel", null, (_, _) => BringLiveToFront());
        menu.Items.Add("Abrir pasta de entrada", null, (_, _) =>
        {
            try
            {
                _app.Services.GetService<SoftPrint.Application.Services.InboxService>()?.OpenFolder();
            }
            catch
            {
                /* ignore */
            }
        });
        menu.Items.Add($"Versão {SoftPrint.Domain.SoftPrintVersion.Current}", null, (_, _) => { });
        menu.Items[^1].Enabled = false;
        menu.Items.Add("Sair", null, (_, _) =>
        {
            NoteExit(AppExitReasons.UserClosed, "Saída pelo menu da bandeja (Sair).");
            _stopHostOnClose = true;
            p.RequestExit();
            _app.Lifetime.StopApplication();
        });

        if (_notify is not null)
            _notify.ContextMenuStrip = menu;

        p.FormClosed += (_, _) =>
        {
            if (!p.ExitRequested && !_stopHostOnClose)
                return;
            NoteExit(AppExitReasons.UserClosed, "Painel fechado com pedido de saída.");
            try { if (_notify is not null) _notify.Visible = false; } catch { /* ignore */ }
            _app.Lifetime.StopApplication();
        };
        _livePanel = p;
        return p;
    }

    private void BringLiveToFront()
    {
        try
        {
            var p = _livePanel;
            if (p is null || p.IsDisposed)
                return;
            if (p.InvokeRequired)
                p.BeginInvoke(BringLiveToFront);
            else
                p.ShowFromTray();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            /* painel já foi fechado */
        }
    }

    private static bool FinishSplash(SplashForm? splash, IUpdateApplier? updateApplier, Task? startupUpdateTask)
    {
        if (splash is null)
            return false;

        splash.WaitUntilReady(
            keepWaiting: () => IsUpdateBusy(startupUpdateTask, updateApplier),
            onTick: () => ApplicationComposer.SyncSplashWithUpdate(splash, updateApplier));

        var skip = updateApplier?.Status.Restarting == true
                   || updateApplier?.Status.InProgress == true;
        splash.CloseSafe();
        splash.Dispose();
        return skip;
    }

    private static bool IsUpdateBusy(Task? startupUpdateTask, IUpdateApplier? updateApplier)
    {
        if (startupUpdateTask is { IsCompleted: false })
            return true;
        var status = updateApplier?.Status;
        return status is not null && (status.Restarting || status.InProgress);
    }

    private void OnApplicationStopping()
    {
        _stopHostOnClose = true;
        try { _livePanel?.RequestExit(); } catch { /* ignore */ }
    }

    private RegisteredWaitHandle? RegisterShowSignal()
    {
        if (_showSignal is null)
            return null;

        return ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, _) => BringLiveToFront(),
            null,
            -1,
            false);
    }

    private void ConfigureInitialPanelVisibility(Dashboard panel)
    {
        if (_startInTray)
        {
            panel.ShowInTaskbar = false;
            panel.Shown += (_, _) => panel.HideToTray(balloon: true);
            return;
        }

        panel.ShowInTaskbar = true;
        panel.WindowState = FormWindowState.Maximized;
        panel.Shown += (_, _) =>
        {
            try
            {
                panel.Activate();
                panel.BringToFront();
            }
            catch
            {
                /* ignore */
            }
        };
    }

    private bool ShouldAbortForRestart(IUpdateApplier? updateApplier, RegisteredWaitHandle? wait)
    {
        while (updateApplier is { Status.Restarting: true } or { Status.InProgress: true })
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(50);
            if (_app.Lifetime.ApplicationStopping.IsCancellationRequested)
                break;
        }

        if (updateApplier?.Status.Restarting != true
            && !_app.Lifetime.ApplicationStopping.IsCancellationRequested)
            return false;

        NoteExit(AppExitReasons.UpdateRestart, "Reinício para aplicar atualização.");
        wait?.Unregister(null);
        return true;
    }

    private void NoteExit(string reason, string? detail = null)
    {
        try { _lifecycle?.NoteExitReason(reason, detail); }
        catch { /* ignore */ }
    }

    private void HookWindowsSessionEvents()
    {
        try
        {
            Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;
            Microsoft.Win32.SystemEvents.SessionEnded += OnSessionEnded;
        }
        catch
        {
            /* SystemEvents pode falhar em alguns ambientes */
        }
    }

    private void UnhookWindowsSessionEvents()
    {
        try
        {
            Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
            Microsoft.Win32.SystemEvents.SessionEnded -= OnSessionEnded;
        }
        catch
        {
            /* ignore */
        }
    }

    private void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        var logoff = e.Reason == Microsoft.Win32.SessionEndReasons.Logoff;
        NoteExit(
            logoff ? AppExitReasons.WindowsLogoff : AppExitReasons.WindowsShutdown,
            logoff ? "Logoff do Windows." : "Desligamento do Windows.");
        // Grava já — o Stop pode não chegar a tempo no desligamento.
        try { _lifecycle?.OnApplicationStopping(); }
        catch { /* ignore */ }
    }

    private void OnSessionEnded(object? sender, Microsoft.Win32.SessionEndedEventArgs e)
    {
        var logoff = e.Reason == Microsoft.Win32.SessionEndReasons.Logoff;
        NoteExit(
            logoff ? AppExitReasons.WindowsLogoff : AppExitReasons.WindowsShutdown,
            e.Reason.ToString());
    }

    private void RunPanelLoop(ref Dashboard panel)
    {
        while (!_app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            ShowPanelIfNeeded(panel);

            System.Windows.Forms.Application.Run(panel);
            if (panel.ExitRequested
                || _stopHostOnClose
                || _app.Lifetime.ApplicationStopping.IsCancellationRequested)
                break;

            try { panel.Dispose(); } catch { /* ignore */ }
            Thread.Sleep(750);
            if (_app.Lifetime.ApplicationStopping.IsCancellationRequested)
                break;

            panel = CreatePanel();
            if (_startInTray)
                panel.HideToTray(balloon: false);
            else
            {
                panel.ShowInTaskbar = true;
                panel.WindowState = FormWindowState.Maximized;
                panel.Show();
            }
        }
    }

    private void ShowPanelIfNeeded(Dashboard panel)
    {
        if (_startInTray)
            return;

        try
        {
            panel.ShowInTaskbar = true;
            panel.WindowState = FormWindowState.Maximized;
            if (!panel.Visible)
                panel.Show();
            panel.Activate();
            panel.BringToFront();
        }
        catch (Exception ex)
        {
            TryShowError("Não foi possível exibir o painel.\n\n" + ex.Message);
        }
    }

    private static void TryShowError(string message)
    {
        try
        {
            MessageBox.Show(message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            /* ignore */
        }
    }
}
