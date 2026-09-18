using AutoPrint.Domain;

namespace AutoPrint.Application.Abstractions;

public interface IEventLogStore
{
    string FolderPath { get; }
    void Write(JobFinishedEventLog entry);
    IReadOnlyList<JobFinishedEventLog> ReadDay(DateOnly day);
    IReadOnlyList<DateOnly> ListDays();
    void OpenFolder();
}

public interface IWebhookRetryQueue
{
    void Enqueue(JobFinishedEventLog entry, string reason);
    IReadOnlyList<WebhookRetryItem> Snapshot();
}

public interface INetworkPrinterDiscovery
{
    Task<IReadOnlyList<DiscoveredNetworkPrinter>> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface IMetricsService
{
    MetricsSnapshot Compute();
}

public sealed record JobFinishedEventLog(
    string EventType,
    DateTimeOffset At,
    Guid Id,
    string Reference,
    string JobType,
    string ContentKind,
    string Status,
    string? PrinterName,
    string? Error,
    string? ErrorReason,
    string? ErrorWhere,
    DateTimeOffset? FinishedAt,
    object? Steps,
    string? Delivery,
    string? DeliveryDetail);

public sealed record WebhookRetryItem(
    Guid Id,
    DateTimeOffset EnqueuedAt,
    int Attempts,
    string Reason,
    JobFinishedEventLog Payload,
    DateTimeOffset NextAttemptAt);

public sealed record DiscoveredNetworkPrinter(string Address, int Port, string Hint, bool Reachable);

public sealed record MetricsSnapshot(
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
