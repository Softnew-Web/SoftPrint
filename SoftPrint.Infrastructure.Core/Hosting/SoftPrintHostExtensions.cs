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
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<WebhookRetryQueue>(sp).Init());
        services.AddSingleton<IWebhookRetryQueue>(sp => sp.GetRequiredService<WebhookRetryQueue>());
        services.AddSingleton<WebhookNotifier>();
        services.AddSingleton<IWebhookNotifier>(sp => sp.GetRequiredService<WebhookNotifier>());
        services.AddSingleton<IPrintStrategy, SimulationPrintStrategy>();
        services.AddSingleton<PrintStrategyResolver>();
        services.AddSingleton<JobQueueService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<InboxService>();
        services.AddHostedService<PrintWorker>();
        services.AddHostedService<InboxFolderWatcher>();
        services.AddHostedService<DataMaintenanceWorker>();
        services.AddHostedService<WebhookRetryWorker>();
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
}
