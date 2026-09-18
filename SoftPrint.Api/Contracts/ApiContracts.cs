namespace SoftPrint.Api.Contracts;

public record SubmitJobRequest(
    string Reference,
    string? Text = null,
    string? JobType = null,
    string? ContentKind = null,
    string? SourcePath = null,
    string? Template = null);

public record SettingsRequest(
    string? PrinterName,
    bool Simulation,
    bool Paused,
    long ExpectedRevision,
    string? ImageFit = null,
    int? ImageScalePercent = null,
    string? PaperSize = null,
    double? PaperWidthMm = null,
    double? PaperHeightMm = null,
    bool? PaperLandscape = null,
    string? InboxFolder = null,
    bool? InboxEnabled = null,
    bool? DeleteInboxAfterPrint = null);

public record StartupRequest(bool Enabled);

public record SystemSettingsRequest(
    int PollIntervalMs,
    int RetentionDays,
    int BackupIntervalMinutes,
    bool SoundEnabled,
    bool LogToFile,
    string? WebhookUrl,
    string? WebhookSecret,
    int WebhookTimeoutMs,
    int WebhookRetrySeconds,
    int WebhookMaxRetries,
    int EventLogRetentionDays,
    int NetworkScanTimeoutMs);

public record PrinterOptionsDto(
    string PrinterName,
    bool Simulation,
    bool Paused,
    long Revision,
    DateTimeOffset UpdatedAt,
    string ImageFit = "contain",
    int ImageScalePercent = 100,
    string PaperSize = "a4",
    double PaperWidthMm = 210,
    double PaperHeightMm = 297,
    bool PaperLandscape = false,
    string InboxFolder = "",
    bool InboxEnabled = false,
    bool DeleteInboxAfterPrint = false);

public record ProcessingStepDto(
    DateTimeOffset At,
    string Stage,
    string Where,
    string Message,
    string? Detail = null,
    bool IsError = false);

public record PrintJobDto(
    Guid Id,
    string Reference,
    string Text,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt = null,
    string? Error = null,
    string? PrinterName = null,
    long? SettingsRevision = null,
    IReadOnlyList<ProcessingStepDto>? Steps = null,
    string? ErrorReason = null,
    string? ErrorWhere = null,
    string JobType = "default",
    string ContentKind = "text",
    string? SourcePath = null,
    string? TemplateName = null,
    Guid? ReprintedFromId = null);

public record PrinterDeviceDto(
    string Name,
    string Port,
    string Connection,
    string Driver,
    bool IsDefault,
    bool IsOffline,
    bool IsNetwork,
    bool IsLocal,
    bool IsShared,
    string Status,
    string DisplayLabel);

public record FeatureFlagsDto(bool SoundEnabled, bool DarkTheme, bool LogToFile, bool StartWithWindows);

public record MetricsDto(
    int TotalJobs,
    int Last24Hours,
    double JobsPerHourLast24h,
    int UncertainTotal,
    double UncertainRatePercent,
    int SimulatedTotal,
    int SentTotal,
    int Pending,
    int Processing,
    string? BusiestJobType);

public record HealthInfo(
    long UptimeSeconds,
    int QueuePending,
    int QueueProcessing,
    int TotalJobs,
    int Uncertain,
    string? LastError,
    bool StartWithWindows,
    bool WebhookConfigured,
    int RetentionDays,
    FeatureFlagsDto Features,
    MetricsDto? Metrics = null);

public record StatusResponse(
    string Application,
    bool Simulation,
    string Printer,
    PrinterOptionsDto Settings,
    HealthInfo Health,
    PlatformCapabilitiesDto Capabilities);

public record PlatformCapabilitiesDto(
    string Platform,
    string PrintingBackend,
    bool HasDesktopShell,
    bool HasNativeFolderPicker,
    string StartupRegistration,
    bool IsLegacy);

public record DiagnoseCheckDto(string Name, bool Available, string? Detail = null);

public record DiagnoseResponse(
    string Edition,
    string Os,
    string Architecture,
    string Runtime,
    string PrintingBackend,
    int PrinterCount,
    PlatformCapabilitiesDto Capabilities,
    IReadOnlyList<DiagnoseCheckDto> Checks);
