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
                System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

                SoftPrint.UI.SplashForm? splash = null;
                if (!startInTray)
                {
                    splash = new SoftPrint.UI.SplashForm(durationMs: 15_000);
                    splash.Show();
                    splash.StartProgress();
                    System.Windows.Forms.Application.DoEvents();
                }

                using var notify = new NotifyIcon
                {
                    Visible = true,
                    Text = $"SoftPrint v{SoftPrint.Domain.SoftPrintVersion.Current}",
                    Icon = SystemIcons.Application,
                    BalloonTipTitle = "SoftPrint"
                };
                tray.Attach(notify);
                using var panel = new Dashboard(address, apiKey, features, notify);
                using var menu = new ContextMenuStrip();
                menu.Items.Add("Abrir painel", null, (_, _) => panel.ShowFromTray());
                menu.Items.Add($"Versão {SoftPrint.Domain.SoftPrintVersion.Current}", null, (_, _) => { });
                menu.Items[^1].Enabled = false;
                menu.Items.Add("Sair", null, (_, _) =>
                {
                    panel.RequestExit();
                    app.Lifetime.StopApplication();
                });
                notify.ContextMenuStrip = menu;
                notify.DoubleClick += (_, _) => panel.ShowFromTray();
                notify.MouseClick += (_, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                        panel.ShowFromTray();
                };
                panel.FormClosed += (_, _) =>
                {
                    notify.Visible = false;
                    app.Lifetime.StopApplication();
                };

                // Garante ~15s de splash com barra (o relógio só começa no StartProgress).
                if (splash is not null)
                {
                    panel.Opacity = 0;
                    panel.ShowInTaskbar = false;
                    splash.WaitUntilFinished();
                    splash.CloseSafe();
                    splash.Dispose();
                    splash = null;
                    panel.ShowInTaskbar = true;
                    panel.WindowState = FormWindowState.Maximized;
                    panel.Opacity = 1;
                }
                else
                {
                    panel.WindowState = FormWindowState.Maximized;
                }

                using var registration = app.Lifetime.ApplicationStopping.Register(() =>
                {
                    splash?.CloseSafe();
                    panel.RequestExit();
                });
                RegisteredWaitHandle? wait = null;
                if (showSignal is not null)
                {
                    wait = ThreadPool.RegisterWaitForSingleObject(
                        showSignal,
                        (_, _) =>
                        {
                            try { panel.BeginInvoke(new Action(panel.ShowFromTray)); }
                            catch (InvalidOperationException) { }
                        },
                        null,
                        -1,
                        false);
                }
                if (startInTray)
                {
                    panel.Opacity = 0;
                    panel.ShowInTaskbar = false;
                    panel.Shown += (_, _) =>
                    {
                        panel.HideToTray(balloon: true);
                        panel.Opacity = 1;
                    };
                }
                try
                {
                    System.Windows.Forms.Application.Run(panel);
                }
                finally
                {
                    wait?.Unregister(null);
                    splash?.CloseSafe();
                    splash?.Dispose();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
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

        try { show.Set(); }
        finally
        {
            current.Dispose();
            legacy.Dispose();
            show.Dispose();
        }
        return null;
    }
}
