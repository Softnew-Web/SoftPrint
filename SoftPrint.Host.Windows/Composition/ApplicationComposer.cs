using System.Security.Cryptography;
using System.Text;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Infrastructure.Auth;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.Infrastructure.Operations;
using SoftPrint.Infrastructure.Printing;
using SoftPrint.UI;

namespace SoftPrint.Composition;

public static class ApplicationComposer
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.AddSoftPrintConfiguration();
        builder.Services.AddSoftPrintCore();
        builder.Services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
        builder.Services.AddSingleton<IPrinterPageMetrics, WindowsPrinterPageMetrics>();
        builder.Services.AddSingleton<TrayAppNotifier>();
        builder.Services.AddSingleton<IAppNotifier>(sp => sp.GetRequiredService<TrayAppNotifier>());
        builder.Services.AddSingleton<IWindowsStartupService, WindowsStartupService>();
        builder.Services.AddSingleton<IFolderOperations, WindowsFolderOperations>();
        builder.Services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
            Platform: "windows",
            PrintingBackend: "windows-spooler",
            HasDesktopShell: true,
            HasNativeFolderPicker: true,
            StartupRegistration: "registry",
            IsLegacy: false));
        builder.Services.AddSingleton<IPrintStrategy, WindowsPrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, ImagePrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, PdfPrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, EscPosPrintStrategy>();
        builder.Services.AddSingleton<INetworkPrinterInstaller, WindowsNetworkPrinterInstaller>();
        builder.Logging.AddSoftPrintFileLogging(builder.Environment, builder.Configuration);

        if (builder.Configuration.GetValue("SoftPrint:StartWithWindows", false))
            new WindowsStartupService(builder.Environment).ApplyFromOptions(true);

        return builder;
    }

    public static WebApplication BuildApplication(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseMiddleware<ApiKeyMiddleware>();
        return app;
    }

    public static void StartDashboardIfNeeded(
        WebApplication app, bool headless, bool startInTray, EventWaitHandle? showSignal)
    {
        if (headless) return;

        var apiKey = app.Services.GetRequiredService<IApiKeyProvider>().ApiKey;
        var tray = app.Services.GetRequiredService<TrayAppNotifier>();
        var features = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SoftPrintFeatureOptions>>().Value;

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Urls.First();
            var thread = new Thread(() =>
            {
                try
                {
                    // Tem de vir ANTES de qualquer Form (splash incluso).
                    // Depois do splash isso lançava e o SoftPrint sumia nos 100%.
                    System.Windows.Forms.Application.SetUnhandledExceptionMode(
                        UnhandledExceptionMode.CatchException);
                }
                catch (InvalidOperationException)
                {
                    /* já definido — seguir */
                }

                System.Windows.Forms.Application.ThreadException += (_, args) =>
                {
                    try
                    {
                        MessageBox.Show(
                            "Erro no painel SoftPrint:\n\n" + args.Exception.Message,
                            "SoftPrint",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    catch { /* ignore */ }
                };

                System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                SoftPrint.UI.SplashForm? splash = null;
                IUpdateApplier? updateApplier = null;
                var autoUpdate = features.AutoUpdateOnStartup && features.UpdateCheckEnabled;
                Task? startupUpdateTask = null;

                if (!startInTray)
                {
                    splash = new SoftPrint.UI.SplashForm(durationMs: 15_000);
                    splash.Show();
                    splash.StartProgress();
                    splash.SetLiveProgress(null, "Buscando versões…", "Procurando se há uma versão nova…");
                    System.Windows.Forms.Application.DoEvents();

                    if (autoUpdate)
                    {
                        var checker = app.Services.GetRequiredService<IUpdateChecker>();
                        updateApplier = app.Services.GetRequiredService<IUpdateApplier>();
                        var applier = updateApplier;
                        startupUpdateTask = Task.Run(async () =>
                        {
                            try
                            {
                                checker.InvalidateCache();
                                splash.SetLiveProgress(null, "Buscando versões…", "Consultando o servidor de atualizações…");
                                var check = await checker.CheckAsync().ConfigureAwait(false);
                                if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
                                {
                                    splash.SetLiveProgress(
                                        null,
                                        "Nenhuma versão nova",
                                        $"Você já está na {check.CurrentVersion}.");
                                    return;
                                }

                                // Update opcional: só avisa. Não baixa/reinicia sozinho (evita fechar o SoftPrint).
                                if (!check.Mandatory)
                                {
                                    splash.SetLiveProgress(
                                        null,
                                        $"Nova versão {check.LatestVersion} disponível",
                                        "Abra o painel e use Atualizar quando quiser instalar.");
                                    return;
                                }

                                splash.SetLiveProgress(
                                    5,
                                    $"Atualização obrigatória {check.LatestVersion}",
                                    "Baixando atualização…");
                                if (!applier.TryStart(out var err) && !string.IsNullOrWhiteSpace(err))
                                {
                                    if (!err.Contains("andamento", StringComparison.OrdinalIgnoreCase))
                                        splash.SetLiveProgress(null, "Atualização adiada", err);
                                }
                            }
                            catch (Exception ex)
                            {
                                splash.SetLiveProgress(null, "Não foi possível buscar versões", ex.Message);
                            }
                        });
                    }
                    else
                    {
                        splash.SetLiveProgress(null, "Abrindo SoftPrint…", "Carregando o painel…");
                    }
                }

                using var notify = new NotifyIcon
                {
                    Visible = true,
                    Text = $"SoftPrint v{SoftPrint.Domain.SoftPrintVersion.Current}",
                    Icon = SystemIcons.Application,
                    BalloonTipTitle = "SoftPrint"
                };
                tray.Attach(notify);

                var stopHostOnClose = false;
                SoftPrint.UI.Dashboard? livePanel = null;

                SoftPrint.UI.Dashboard CreatePanel()
                {
                    var p = new SoftPrint.UI.Dashboard(address, apiKey, features, tray);
                    var menu = new ContextMenuStrip();
                    menu.Items.Add("Abrir painel", null, (_, _) => BringLiveToFront());
                    menu.Items.Add($"Versão {SoftPrint.Domain.SoftPrintVersion.Current}", null, (_, _) => { });
                    menu.Items[^1].Enabled = false;
                    menu.Items.Add("Sair", null, (_, _) =>
                    {
                        stopHostOnClose = true;
                        p.RequestExit();
                        app.Lifetime.StopApplication();
                    });
                    notify.ContextMenuStrip = menu;
                    p.FormClosed += (_, _) =>
                    {
                        if (p.ExitRequested || stopHostOnClose)
                        {
                            try { notify.Visible = false; } catch { /* ignore */ }
                            app.Lifetime.StopApplication();
                        }
                    };
                    livePanel = p;
                    return p;
                }

                void BringLiveToFront()
                {
                    try
                    {
                        var p = livePanel;
                        if (p is null || p.IsDisposed) return;
                        if (p.InvokeRequired)
                            p.BeginInvoke(BringLiveToFront);
                        else
                            p.ShowFromTray();
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }

                notify.DoubleClick += (_, _) => BringLiveToFront();
                notify.MouseDoubleClick += (_, _) => BringLiveToFront();

                var panel = CreatePanel();

                // Mínimo ~15s de splash; alonga se ainda estiver baixando atualização.
                // Não mostrar/ocultar o painel com Opacity durante o splash — isso criava
                // janela layered e, em alguns PCs, o painel nunca aparecia depois dos 100%.
                var skipDashboard = false;
                if (splash is not null)
                {
                    splash.WaitUntilReady(
                        keepWaiting: () =>
                        {
                            if (startupUpdateTask is { IsCompleted: false })
                                return true;
                            var status = updateApplier?.Status;
                            if (status is null) return false;
                            if (status.Restarting) return true;
                            return status.InProgress;
                        },
                        onTick: () =>
                        {
                            var status = updateApplier?.Status;
                            if (status is null || (!status.InProgress && !status.Restarting && !status.Failed))
                                return;
                            if (status.Failed)
                            {
                                splash.SetLiveProgress(
                                    status.Percent,
                                    "Falha na atualização",
                                    status.Error ?? status.Message);
                                return;
                            }
                            splash.SetLiveProgress(
                                status.Percent,
                                status.Restarting ? "Reiniciando SoftPrint…" : "Baixando nova versão…",
                                status.Message);
                        });

                    skipDashboard = updateApplier?.Status.Restarting == true
                        || updateApplier?.Status.InProgress == true;

                    splash.CloseSafe();
                    splash.Dispose();
                    splash = null;
                }

                using var registration = app.Lifetime.ApplicationStopping.Register(() =>
                {
                    stopHostOnClose = true;
                    splash?.CloseSafe();
                    try { livePanel?.RequestExit(); } catch { /* ignore */ }
                });
                RegisteredWaitHandle? wait = null;
                if (showSignal is not null)
                {
                    wait = ThreadPool.RegisterWaitForSingleObject(
                        showSignal,
                        (_, _) => BringLiveToFront(),
                        null,
                        -1,
                        false);
                }
                if (startInTray)
                {
                    panel.ShowInTaskbar = false;
                    panel.Shown += (_, _) => panel.HideToTray(balloon: true);
                }
                else
                {
                    panel.ShowInTaskbar = true;
                    panel.WindowState = FormWindowState.Maximized;
                    panel.Shown += (_, _) =>
                    {
                        try
                        {
                            panel.Activate();
                            panel.BringToFront();
                        }
                        catch { /* ignore */ }
                    };
                }

                if (skipDashboard)
                {
                    // Atualização obrigatória a reiniciar — espera; se falhar, abre o painel.
                    while (updateApplier is { Status: { Restarting: true } } ||
                           updateApplier is { Status: { InProgress: true } })
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(50);
                        if (app.Lifetime.ApplicationStopping.IsCancellationRequested)
                            break;
                    }

                    if (updateApplier?.Status.Restarting == true ||
                        app.Lifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        wait?.Unregister(null);
                        return;
                    }
                }

                try
                {
                    // Recria o painel se cair sem o usuário ter pedido Sair.
                    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        if (!startInTray)
                        {
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
                                MessageBox.Show(
                                    "Não foi possível exibir o painel.\n\n" + ex.Message,
                                    "SoftPrint",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Error);
                            }
                        }

                        System.Windows.Forms.Application.Run(panel);
                        if (panel.ExitRequested ||
                            stopHostOnClose ||
                            app.Lifetime.ApplicationStopping.IsCancellationRequested)
                            break;

                        try { panel.Dispose(); } catch { /* ignore */ }
                        Thread.Sleep(750);
                        if (app.Lifetime.ApplicationStopping.IsCancellationRequested)
                            break;

                        panel = CreatePanel();
                        if (startInTray)
                            panel.HideToTray(balloon: false);
                        else
                        {
                            panel.ShowInTaskbar = true;
                            panel.WindowState = FormWindowState.Maximized;
                            panel.Show();
                        }
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        MessageBox.Show(
                            "O SoftPrint encerrou após o carregamento.\n\n" + ex.Message,
                            "SoftPrint",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    catch { /* ignore */ }
                }
                finally
                {
                    wait?.Unregister(null);
                    splash?.CloseSafe();
                    splash?.Dispose();
                    try { notify.Visible = false; } catch { /* ignore */ }
                    try { livePanel?.Dispose(); } catch { /* ignore */ }
                    app.Lifetime.StopApplication();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = false;
            thread.Start();
        });
    }
}

public sealed class SoftPrintInstance : IDisposable
{
    private readonly Mutex _current;
    private readonly Mutex _legacy;

    public EventWaitHandle ShowRequested { get; }

    internal SoftPrintInstance(Mutex current, Mutex legacy, EventWaitHandle showRequested)
    {
        _current = current;
        _legacy = legacy;
        ShowRequested = showRequested;
    }

    public void Dispose()
    {
        _current.Dispose();
        _legacy.Dispose();
        ShowRequested.Dispose();
    }
}

public static class SingleInstanceGuard
{
    public static SoftPrintInstance? TryAcquire()
    {
        var suffix = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..24];
        var current = new Mutex(true, "Local\\SoftPrint-" + suffix, out var currentAcquired);
        var legacy = new Mutex(true, "Local\\AutoPrint-" + suffix, out var legacyAcquired);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SoftPrint-Show-" + suffix);
        if (currentAcquired && legacyAcquired)
            return new SoftPrintInstance(current, legacy, show);

        // Named wait handle: não Dispose o kernel object aqui — só o handle local.
        // Dispose total no processo secundário podia interferir no listener da 1ª instância.
        try { show.Set(); }
        finally
        {
            current.Dispose();
            legacy.Dispose();
            // Mantém o evento nomeado vivo para a instância principal.
            show.Close();
        }
        return null;
    }
}
