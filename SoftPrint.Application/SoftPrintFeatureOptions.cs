namespace SoftPrint.Application;

/// <summary>Opções carregadas do .env / appsettings.</summary>
public sealed class SoftPrintFeatureOptions
{
    public const string Section = "SoftPrint";

    public bool Simulation { get; set; } = true;
    public string PrinterName { get; set; } = "";
    public int PollIntervalMs { get; set; } = 100;
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
    public int EventLogRetentionDays { get; set; } = 2;
    public string InboxFolder { get; set; } = "";
    public bool InboxEnabled { get; set; }
    public bool DeleteInboxAfterPrint { get; set; }
    public int NetworkScanTimeoutMs { get; set; } = 400;
    public string PrinterRoutes { get; set; } = "";
    public string Templates { get; set; } = "";

    /// <summary>Consulta releases no GitHub para avisar clientes sobre atualização.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;
    public string UpdateGitHubOwner { get; set; } = "Softnew-Web";
    public string UpdateGitHubRepo { get; set; } = "SoftPrint";
    /// <summary>Token opcional (repo privado). Preferir variável de ambiente UPDATE_GITHUB_TOKEN / GITHUB_TOKEN.</summary>
    public string UpdateGitHubToken { get; set; } = "";
    public int UpdateCacheMinutes { get; set; } = 360;
    /// <summary>Nome preferido do asset no release (instalador Windows).</summary>
    public string UpdateAssetName { get; set; } = "SoftPrint-Setup.exe";
    /// <summary>Se true, qualquer versão mais nova é tratada como obrigatória.</summary>
    public bool UpdateAlwaysMandatory { get; set; }
    /// <summary>No arranque, se houver versão nova *obrigatória*, baixa e aplica automaticamente. Aviso de versão nova continua mesmo com false.</summary>
    public bool AutoUpdateOnStartup { get; set; }

    /// <summary>Opt-in: envia telemetria anônima de falhas de impressão (status uncertain).</summary>
    public bool TelemetryEnabled { get; set; }
    /// <summary>URL POST para telemetria (somente se TelemetryEnabled).</summary>
    public string TelemetryUrl { get; set; } = "";

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
