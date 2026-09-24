using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface IEventLogStore
{
    string FolderPath { get; }
    void Write(JobFinishedEventLog entry);
    IReadOnlyList<JobFinishedEventLog> ReadDay(DateOnly day);
    IReadOnlyList<DateOnly> ListDays();
    void OpenFolder();
}

/// <summary>Registra início/fim do SoftPrint, fechamento, desligamento do Windows e crashes.</summary>
public interface IAppLifecycleLogger
{
    /// <summary>Chamado no arranque: detecta saída suja anterior e grava "iniciado".</summary>
    void OnApplicationStarted();

    /// <summary>Marca o motivo do encerramento (usado no Stop).</summary>
    void NoteExitReason(string reason, string? detail = null);

    /// <summary>Grava encerramento limpo e limpa o marcador de sessão.</summary>
    void OnApplicationStopping();

    /// <summary>Melhor esforço: grava crash (pode ser chamado fora do DI em handlers estáticos).</summary>
    void OnCrash(string message, string? detail = null);

    /// <summary>Registra crash a partir da exceção (tipo, mensagem, stack e internas).</summary>
    void OnCrash(Exception exception, string? context = null);
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

/// <summary>Motivos conhecidos de saída do SoftPrint.</summary>
public static class AppExitReasons
{
    public const string UserClosed = "user-closed";
    public const string WindowsShutdown = "windows-shutdown";
    public const string WindowsLogoff = "windows-logoff";
    public const string HostStop = "host-stop";
    public const string Crashed = "crashed";
    public const string UncleanExit = "unclean-exit";
    public const string UpdateRestart = "update-restart";
}
