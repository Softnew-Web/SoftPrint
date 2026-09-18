using System.Security.Cryptography;
using System.Text;
using AutoPrint.Application;
using AutoPrint.Application.Abstractions;
using AutoPrint.Application.Services;
using AutoPrint.Application.Workers;
using AutoPrint.Domain;
using AutoPrint.Infrastructure.Auth;
using AutoPrint.Infrastructure.Configuration;
using AutoPrint.Infrastructure.Integrations;
using AutoPrint.Infrastructure.Operations;
using AutoPrint.Infrastructure.Persistence;
using AutoPrint.Infrastructure.Printing;
using AutoPrint.UI;

namespace AutoPrint.Composition;

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

        builder.Configuration.AddAutoPrintEnvFile(builder.Environment.ContentRootPath);
        builder.Services.Configure<AutoPrintFeatureOptions>(builder.Configuration.GetSection(AutoPrintFeatureOptions.Section));

        var statusTypes = JobStatusTypeCatalogFactory.FromConfiguration(builder.Configuration);
        JobStatusExtensions.UseCatalog(statusTypes);

        builder.Services.AddHttpClient("autoprinthook");
        builder.Services.AddSingleton(statusTypes);
        builder.Services.AddSingleton<IApiKeyProvider, FileApiKeyProvider>();
        builder.Services.AddSingleton<IJobRepository, JsonJobRepository>();
        builder.Services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        builder.Services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
        builder.Services.AddSingleton<INetworkPrinterDiscovery, NetworkPrinterDiscovery>();
        builder.Services.AddSingleton<IPrintJobFactory, PrintJobFactory>();
        builder.Services.AddSingleton<ITemplateRenderer, TemplateRenderer>();
        builder.Services.AddSingleton<IPrinterRouter, PrinterRouter>();
        builder.Services.AddSingleton<IEventLogStore, EventLogStore>();
        builder.Services.AddSingleton(sp =>
            ActivatorUtilities.CreateInstance<WebhookRetryQueue>(sp).Init());
        builder.Services.AddSingleton<IWebhookRetryQueue>(sp => sp.GetRequiredService<WebhookRetryQueue>());
        builder.Services.AddSingleton<WebhookNotifier>();
        builder.Services.AddSingleton<IWebhookNotifier>(sp => sp.GetRequiredService<WebhookNotifier>());
        builder.Services.AddSingleton<TrayAppNotifier>();
        builder.Services.AddSingleton<IAppNotifier>(sp => sp.GetRequiredService<TrayAppNotifier>());
        builder.Services.AddSingleton<IWindowsStartupService, WindowsStartupService>();
        builder.Services.AddSingleton<IMetricsService, MetricsService>();
        builder.Services.AddSingleton<IPrintStrategy, SimulationPrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, WindowsPrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, ImagePrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, PdfPrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, EscPosPrintStrategy>();
        builder.Services.AddSingleton<PrintStrategyResolver>();
        builder.Services.AddSingleton<JobQueueService>();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<InboxService>();
        builder.Services.AddHostedService<PrintWorker>();
        builder.Services.AddHostedService<InboxFolderWatcher>();
        builder.Services.AddHostedService<DataMaintenanceWorker>();
        builder.Services.AddHostedService<WebhookRetryWorker>();

        if (builder.Configuration.GetValue("AutoPrint:LogToFile", true))
            builder.Logging.AddProvider(new FileLogProvider(builder.Environment,
                Microsoft.Extensions.Options.Options.Create(
                    builder.Configuration.GetSection(AutoPrintFeatureOptions.Section).Get<AutoPrintFeatureOptions>()
                    ?? new AutoPrintFeatureOptions())));

        var startup = new WindowsStartupService(builder.Environment);
        startup.ApplyFromOptions(builder.Configuration.GetValue("AutoPrint:StartWithWindows", false));

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
        var features = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AutoPrintFeatureOptions>>().Value;

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
                    Text = "AutoPrint",
                    Icon = SystemIcons.Application,
                    BalloonTipTitle = "AutoPrint"
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
        var instanceName = "Local\\AutoPrint-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..24];
        var mutex = new Mutex(true, instanceName, out acquired);
        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }
        return mutex;
    }
}
