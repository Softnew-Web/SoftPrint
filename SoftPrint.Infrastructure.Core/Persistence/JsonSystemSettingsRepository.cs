using System.Text.Json;
using System.Text.Json.Nodes;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Persistence;

public sealed class JsonSystemSettingsRepository : ISystemSettingsRepository
{
    private readonly object _gate = new();
    private readonly string _path;
    private SystemSettings _current;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonSystemSettingsRepository(IAppPaths paths, IOptions<SoftPrintFeatureOptions> defaults)
    {
        _path = Path.Combine(paths.ConfigRoot, "system-settings.json");
        var value = defaults.Value;
        _current = Clamp(new SystemSettings(
            value.PollIntervalMs, value.RetentionDays, value.BackupIntervalMinutes,
            value.SoundEnabled, value.LogToFile, value.WebhookUrl, value.WebhookSecret,
            value.WebhookTimeoutMs, value.WebhookRetrySeconds, value.WebhookMaxRetries,
            value.EventLogRetentionDays, value.NetworkScanTimeoutMs,
            value.TelemetryEnabled, value.TelemetryUrl ?? ""));
        LoadFromDisk();
    }

    public SystemSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public SystemSettings Update(SystemSettings settings)
    {
        lock (_gate)
        {
            _current = Clamp(settings);
            var root = new JsonObject
            {
                ["SoftPrint"] = JsonSerializer.SerializeToNode(_current, JsonOptions)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, root.ToJsonString(JsonOptions));
            return _current;
        }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;
            var section = root.TryGetProperty("SoftPrint", out var wrapped) ? wrapped : root;
            var loaded = JsonSerializer.Deserialize<SystemSettings>(section.GetRawText(), JsonOptions);
            if (loaded is not null)
                _current = Clamp(loaded);
        }
        catch
        {
            /* keep defaults */
        }
    }

    private static SystemSettings Clamp(SystemSettings settings) => settings with
    {
        PollIntervalMs = Math.Clamp(settings.PollIntervalMs, 100, 60_000),
        RetentionDays = Math.Clamp(settings.RetentionDays, 1, 3650),
        BackupIntervalMinutes = Math.Clamp(settings.BackupIntervalMinutes, 5, 1440),
        WebhookTimeoutMs = Math.Clamp(settings.WebhookTimeoutMs, 500, 120_000),
        WebhookRetrySeconds = Math.Clamp(settings.WebhookRetrySeconds, 1, 3600),
        WebhookMaxRetries = Math.Clamp(settings.WebhookMaxRetries, 0, 100),
        EventLogRetentionDays = Math.Clamp(settings.EventLogRetentionDays, 1, 3650),
        NetworkScanTimeoutMs = Math.Clamp(settings.NetworkScanTimeoutMs, 50, 30_000),
        TelemetryUrl = settings.TelemetryUrl?.Trim() ?? ""
    };
}
