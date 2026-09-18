namespace AutoPrint.Application;

/// <summary>Opções carregadas do .env / appsettings.</summary>
public sealed class AutoPrintFeatureOptions
{
    public const string Section = "AutoPrint";

    public bool Simulation { get; set; } = true;
    public string PrinterName { get; set; } = "";
    public int PollIntervalMs { get; set; } = 500;
    public bool StartWithWindows { get; set; }
    public int RetentionDays { get; set; } = 30;
    public int BackupIntervalMinutes { get; set; } = 60;
    public bool SoundEnabled { get; set; } = true;
    public bool DarkTheme { get; set; }
    public bool LogToFile { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public int WebhookTimeoutMs { get; set; } = 5000;
    public int WebhookRetrySeconds { get; set; } = 15;
    public int WebhookMaxRetries { get; set; } = 8;
    public string EventLogFolder { get; set; } = "logs";
    public int EventLogRetentionDays { get; set; } = 30;
    public string InboxFolder { get; set; } = "";
    public bool InboxEnabled { get; set; }
    public bool DeleteInboxAfterPrint { get; set; }
    public int NetworkScanTimeoutMs { get; set; } = 400;
    public string PrinterRoutes { get; set; } = "";
    public string Templates { get; set; } = "";

    public IReadOnlyDictionary<string, string> ParseRoutes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in PrinterRoutes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            map[part[..idx].Trim()] = part[(idx + 1)..].Trim();
        }
        return map;
    }

    public IReadOnlyDictionary<string, string> ParseTemplates()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in Templates.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            map[part[..idx].Trim()] = part[(idx + 1)..].Trim().Replace("\\n", "\n");
        }
        return map;
    }
}
