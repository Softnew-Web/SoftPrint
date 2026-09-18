using Microsoft.Extensions.Configuration;

namespace SoftPrint.Infrastructure.Configuration;

/// <summary>Carrega um arquivo .env (KEY=VALUE) para o IConfiguration.</summary>
public static class EnvFileConfigurationExtensions
{
    private static readonly Dictionary<string, string> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["URLS"] = "Urls",
        ["HEADLESS"] = "headless",
        ["SIMULATION"] = "SoftPrint:Simulation",
        ["PRINTER_NAME"] = "SoftPrint:PrinterName",
        ["POLL_INTERVAL_MS"] = "SoftPrint:PollIntervalMs",
        ["START_WITH_WINDOWS"] = "SoftPrint:StartWithWindows",
        ["RETENTION_DAYS"] = "SoftPrint:RetentionDays",
        ["BACKUP_INTERVAL_MINUTES"] = "SoftPrint:BackupIntervalMinutes",
        ["SOUND_ENABLED"] = "SoftPrint:SoundEnabled",
        ["DARK_THEME"] = "SoftPrint:DarkTheme",
        ["LOG_TO_FILE"] = "SoftPrint:LogToFile",
        ["WEBHOOK_URL"] = "SoftPrint:WebhookUrl",
        ["WEBHOOK_SECRET"] = "SoftPrint:WebhookSecret",
        ["WEBHOOK_TIMEOUT_MS"] = "SoftPrint:WebhookTimeoutMs",
        ["WEBHOOK_RETRY_SECONDS"] = "SoftPrint:WebhookRetrySeconds",
        ["WEBHOOK_MAX_RETRIES"] = "SoftPrint:WebhookMaxRetries",
        ["EVENT_LOG_FOLDER"] = "SoftPrint:EventLogFolder",
        ["EVENT_LOG_RETENTION_DAYS"] = "SoftPrint:EventLogRetentionDays",
        ["INBOX_FOLDER"] = "SoftPrint:InboxFolder",
        ["INBOX_ENABLED"] = "SoftPrint:InboxEnabled",
        ["DELETE_INBOX_AFTER_PRINT"] = "SoftPrint:DeleteInboxAfterPrint",
        ["NETWORK_SCAN_TIMEOUT_MS"] = "SoftPrint:NetworkScanTimeoutMs",
        ["PRINTER_ROUTES"] = "SoftPrint:PrinterRoutes",
        ["TEMPLATES"] = "SoftPrint:Templates",
        ["STATUS_PENDING_WIRE"] = "SoftPrint:Statuses:Pending:Wire",
        ["STATUS_PENDING_LABEL"] = "SoftPrint:Statuses:Pending:Label",
        ["STATUS_PROCESSING_WIRE"] = "SoftPrint:Statuses:Processing:Wire",
        ["STATUS_PROCESSING_LABEL"] = "SoftPrint:Statuses:Processing:Label",
        ["STATUS_SIMULATED_WIRE"] = "SoftPrint:Statuses:Simulated:Wire",
        ["STATUS_SIMULATED_LABEL"] = "SoftPrint:Statuses:Simulated:Label",
        ["STATUS_SENT_WIRE"] = "SoftPrint:Statuses:Sent:Wire",
        ["STATUS_SENT_LABEL"] = "SoftPrint:Statuses:Sent:Label",
        ["STATUS_UNCERTAIN_WIRE"] = "SoftPrint:Statuses:Uncertain:Wire",
        ["STATUS_UNCERTAIN_LABEL"] = "SoftPrint:Statuses:Uncertain:Label"
    };

    public static IConfigurationBuilder AddLegacyAutoPrintAliases(this IConfigurationBuilder builder)
    {
        var current = builder.Build();
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in current.GetSection("AutoPrint").AsEnumerable())
        {
            if (pair.Value is null) continue;
            if (pair.Key.Equals("AutoPrint", StringComparison.OrdinalIgnoreCase)) continue;
            if (!pair.Key.StartsWith("AutoPrint", StringComparison.OrdinalIgnoreCase)) continue;
            var mapped = "SoftPrint" + pair.Key["AutoPrint".Length..];
            if (current[mapped] is null)
                data[mapped] = pair.Value;
        }

        return data.Count == 0 ? builder : builder.AddInMemoryCollection(data);
    }

    public static IConfigurationBuilder AddSoftPrintEnvFile(this IConfigurationBuilder builder, string contentRoot)
    {
        var path = Path.Combine(contentRoot, ".env");
        if (!File.Exists(path)) return builder;

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;

            var rawKey = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"').Trim('\'');
            if (KeyMap.TryGetValue(rawKey, out var mapped))
                data[mapped] = value;
            else
                data[rawKey.Replace("__", ":")] = value;
        }

        return builder.AddInMemoryCollection(data);
    }
}
