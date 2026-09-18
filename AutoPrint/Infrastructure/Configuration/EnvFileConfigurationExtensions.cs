using Microsoft.Extensions.Configuration;

namespace AutoPrint.Infrastructure.Configuration;

/// <summary>Carrega um arquivo .env (KEY=VALUE) para o IConfiguration.</summary>
public static class EnvFileConfigurationExtensions
{
    private static readonly Dictionary<string, string> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["URLS"] = "Urls",
        ["HEADLESS"] = "headless",
        ["SIMULATION"] = "AutoPrint:Simulation",
        ["PRINTER_NAME"] = "AutoPrint:PrinterName",
        ["POLL_INTERVAL_MS"] = "AutoPrint:PollIntervalMs",
        ["START_WITH_WINDOWS"] = "AutoPrint:StartWithWindows",
        ["RETENTION_DAYS"] = "AutoPrint:RetentionDays",
        ["BACKUP_INTERVAL_MINUTES"] = "AutoPrint:BackupIntervalMinutes",
        ["SOUND_ENABLED"] = "AutoPrint:SoundEnabled",
        ["DARK_THEME"] = "AutoPrint:DarkTheme",
        ["LOG_TO_FILE"] = "AutoPrint:LogToFile",
        ["WEBHOOK_URL"] = "AutoPrint:WebhookUrl",
        ["WEBHOOK_SECRET"] = "AutoPrint:WebhookSecret",
        ["WEBHOOK_TIMEOUT_MS"] = "AutoPrint:WebhookTimeoutMs",
        ["WEBHOOK_RETRY_SECONDS"] = "AutoPrint:WebhookRetrySeconds",
        ["WEBHOOK_MAX_RETRIES"] = "AutoPrint:WebhookMaxRetries",
        ["EVENT_LOG_FOLDER"] = "AutoPrint:EventLogFolder",
        ["EVENT_LOG_RETENTION_DAYS"] = "AutoPrint:EventLogRetentionDays",
        ["INBOX_FOLDER"] = "AutoPrint:InboxFolder",
        ["INBOX_ENABLED"] = "AutoPrint:InboxEnabled",
        ["DELETE_INBOX_AFTER_PRINT"] = "AutoPrint:DeleteInboxAfterPrint",
        ["NETWORK_SCAN_TIMEOUT_MS"] = "AutoPrint:NetworkScanTimeoutMs",
        ["PRINTER_ROUTES"] = "AutoPrint:PrinterRoutes",
        ["TEMPLATES"] = "AutoPrint:Templates",
        ["STATUS_PENDING_WIRE"] = "AutoPrint:Statuses:Pending:Wire",
        ["STATUS_PENDING_LABEL"] = "AutoPrint:Statuses:Pending:Label",
        ["STATUS_PROCESSING_WIRE"] = "AutoPrint:Statuses:Processing:Wire",
        ["STATUS_PROCESSING_LABEL"] = "AutoPrint:Statuses:Processing:Label",
        ["STATUS_SIMULATED_WIRE"] = "AutoPrint:Statuses:Simulated:Wire",
        ["STATUS_SIMULATED_LABEL"] = "AutoPrint:Statuses:Simulated:Label",
        ["STATUS_SENT_WIRE"] = "AutoPrint:Statuses:Sent:Wire",
        ["STATUS_SENT_LABEL"] = "AutoPrint:Statuses:Sent:Label",
        ["STATUS_UNCERTAIN_WIRE"] = "AutoPrint:Statuses:Uncertain:Wire",
        ["STATUS_UNCERTAIN_LABEL"] = "AutoPrint:Statuses:Uncertain:Label"
    };

    public static IConfigurationBuilder AddAutoPrintEnvFile(this IConfigurationBuilder builder, string contentRoot)
    {
        EnsureEnvFile(contentRoot);
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

    private static void EnsureEnvFile(string contentRoot)
    {
        var envPath = Path.Combine(contentRoot, ".env");
        if (File.Exists(envPath)) return;

        var example = Path.Combine(contentRoot, ".env.example");
        if (File.Exists(example))
            File.Copy(example, envPath);
    }
}
