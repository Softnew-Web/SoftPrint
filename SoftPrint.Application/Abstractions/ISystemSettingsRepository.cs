namespace SoftPrint.Application.Abstractions;

public sealed record SystemSettings(
    int PollIntervalMs,
    int RetentionDays,
    int BackupIntervalMinutes,
    bool SoundEnabled,
    bool LogToFile,
    string WebhookUrl,
    string WebhookSecret,
    int WebhookTimeoutMs,
    int WebhookRetrySeconds,
    int WebhookMaxRetries,
    int EventLogRetentionDays,
    int NetworkScanTimeoutMs,
    bool TelemetryEnabled = false,
    string TelemetryUrl = "",
    string PrinterRoutes = "");

public interface ISystemSettingsRepository
{
    SystemSettings Current { get; }
    SystemSettings Update(SystemSettings settings);
}
