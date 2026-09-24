using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Application.Workers;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Configuration;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.Infrastructure.Operations;
using SoftPrint.Infrastructure.Persistence;
using SoftPrint.Infrastructure.Printing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Hosting;

public static class SoftPrintHostExtensions
{
    public static WebApplicationBuilder AddSoftPrintConfiguration(this WebApplicationBuilder builder)
    {
        var userConfigRoot = UserAppPaths.ResolveConfigRoot();
        Directory.CreateDirectory(userConfigRoot);
        builder.Configuration.AddJsonFile(
            Path.Combine(userConfigRoot, "system-settings.json"),
            optional: true,
            reloadOnChange: true);
        builder.Configuration.AddSoftPrintEnvFile(builder.Environment.ContentRootPath);
        builder.Configuration.AddLegacyAutoPrintAliases();
        builder.Services.Configure<SoftPrintFeatureOptions>(
            builder.Configuration.GetSection(SoftPrintFeatureOptions.Section));

        var statusTypes = JobStatusTypeCatalogFactory.FromConfiguration(builder.Configuration);
        JobStatusExtensions.UseCatalog(statusTypes);
        builder.Services.AddSingleton(statusTypes);
        return builder;
    }

    public static IServiceCollection AddSoftPrintCore(this IServiceCollection services)
    {
        services.AddHttpClient("softprintthook");
        services.AddHttpClient("softprint-update", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
        });
        services.AddHttpClient("softprint-update-download", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(30);
        });
        services.AddHttpClient("softprint-telemetry", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(8);
        });
        services.AddSingleton<IAppPaths, UserAppPaths>();
        services.AddSingleton<IApiKeyProvider, FileApiKeyProvider>();
        services.AddSingleton<IJobRepository, JsonJobRepository>();
        services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        services.AddSingleton<ISystemSettingsRepository, JsonSystemSettingsRepository>();
        services.AddSingleton<IPrintJobFactory, PrintJobFactory>();
        services.AddSingleton<ITemplateRenderer, TemplateRenderer>();
        services.AddSingleton<IPrinterRouter, PrinterRouter>();
        services.AddSingleton<INetworkPrinterDiscovery, NetworkPrinterDiscovery>();
        services.AddSingleton<IMetricsService, MetricsService>();
        services.AddSingleton<IEventLogStore, EventLogStore>();
        services.AddSingleton<IAppLifecycleLogger, AppLifecycleLogger>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<WebhookRetryQueue>(sp).Init());
        services.AddSingleton<IWebhookRetryQueue>(sp => sp.GetRequiredService<WebhookRetryQueue>());
        services.AddSingleton<WebhookNotifier>();
        services.AddSingleton<IWebhookNotifier>(sp => sp.GetRequiredService<WebhookNotifier>());
        services.AddSingleton<ITelemetryService, SoftPrint.Infrastructure.Integrations.TelemetryService>();
        services.AddSingleton<ISupportBundleService, SoftPrint.Infrastructure.Operations.SupportBundleService>();
        services.AddSingleton<IPrintStrategy, SimulationPrintStrategy>();
        services.AddSingleton<PrintStrategyResolver>();
        services.AddSingleton<JobQueueService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<InboxService>();
        services.AddSingleton<IUpdateChecker, SoftPrint.Infrastructure.Updates.GitHubReleaseUpdateChecker>();
        services.AddSingleton<IPreviousVersionStore, SoftPrint.Infrastructure.Updates.LocalPreviousVersionStore>();
        services.AddSingleton<IUpdateHistoryStore, SoftPrint.Infrastructure.Updates.LocalUpdateHistoryStore>();
        services.AddSingleton<IUpdateApplier, SoftPrint.Infrastructure.Updates.SoftPrintUpdateApplier>();
        services.AddHostedService<SoftPrint.Infrastructure.Updates.UpdateCheckHostedService>();
        services.AddHostedService<AppLifecycleHostedService>();
        services.AddHostedService<PrintWorker>();
        services.AddHostedService<InboxFolderWatcher>();
        services.AddHostedService<DataMaintenanceWorker>();
        services.AddHostedService<WebhookRetryWorker>();
        services.AddHostedService<SoftPrint.Infrastructure.Operations.QueueHealthHostedService>();
        services.AddHostedService<SoftPrint.Infrastructure.Operations.TelemetryHeartbeatHostedService>();
        return services;
    }

    public static ILoggingBuilder AddSoftPrintFileLogging(
        this ILoggingBuilder logging, IHostEnvironment environment, IConfiguration configuration)
    {
        if (!configuration.GetValue("SoftPrint:LogToFile", true)) return logging;
        var options = configuration.GetSection(SoftPrintFeatureOptions.Section).Get<SoftPrintFeatureOptions>()
                      ?? new SoftPrintFeatureOptions();
        logging.AddProvider(new FileLogProvider(environment, Options.Create(options)));
        return logging;
    }

    /// <summary>
    /// Consome falha de atualização pendente e devolve o texto para a UI (WinForms/etc.).
    /// Null se não houver aviso.
    /// </summary>
    public static string? TryConsumeUpdateFailureMessage(IServiceProvider services)
    {
        var updateError = SoftPrint.Infrastructure.Updates.UpdateFailureNotice.TryConsume();
        if (string.IsNullOrWhiteSpace(updateError))
            return null;

        var detail = updateError;
        try
        {
            var previous = services.GetService<IPreviousVersionStore>()?.TryGet();
            if (previous is not null)
            {
                detail +=
                    $"\n\nHá uma versão anterior local (v{previous.Version}). " +
                    "Em Configurações → Sobre você pode retroceder.";
            }
        }
        catch
        {
            /* ignore */
        }

        return "A última atualização do SoftPrint não concluiu corretamente.\n\n" + detail;
    }
}
