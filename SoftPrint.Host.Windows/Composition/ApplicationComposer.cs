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

    public static void StartDashboardIfNeeded(WebApplication app, bool headless)
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
                using var notify = new NotifyIcon
                {
                    Visible = true,
                    Text = "SoftPrint",
                    Icon = SystemIcons.Application,
                    BalloonTipTitle = "SoftPrint"
                };
                tray.Attach(notify);
                using var panel = new Dashboard(address, apiKey, features, notify);
                panel.FormClosed += (_, _) =>
                {
                    notify.Visible = false;
                    app.Lifetime.StopApplication();
                };
                using var registration = app.Lifetime.ApplicationStopping.Register(() =>
                {
                    if (panel.IsHandleCreated && !panel.IsDisposed)
                        try { panel.BeginInvoke(() => panel.Close()); }
                        catch (InvalidOperationException) { }
                });
                System.Windows.Forms.Application.Run(panel);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        });
    }
}

public static class SingleInstanceGuard
{
    public static IDisposable? TryAcquire(out bool acquired)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..24];
        var current = new Mutex(true, "Local\\SoftPrint-" + suffix, out var currentAcquired);
        var legacy = new Mutex(true, "Local\\AutoPrint-" + suffix, out var legacyAcquired);
        acquired = currentAcquired && legacyAcquired;
        if (!acquired)
        {
            current.Dispose();
            legacy.Dispose();
            return null;
        }
        return new MutexPair(current, legacy);
    }

    private sealed class MutexPair(Mutex current, Mutex legacy) : IDisposable
    {
        public void Dispose()
        {
            current.Dispose();
            legacy.Dispose();
        }
    }
}
