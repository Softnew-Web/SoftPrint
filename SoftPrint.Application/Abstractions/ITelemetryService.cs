using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface ITelemetryService
{
    /// <summary>Envia telemetria de falha (uncertain). Nunca lança; no-op se desabilitado.</summary>
    Task ReportPrintFailureAsync(PrintJob job, CancellationToken cancellationToken = default);

    /// <summary>Heartbeat de frota (versão, fila, plataforma). Opt-in via TelemetryEnabled.</summary>
    Task ReportHeartbeatAsync(
        int pending,
        int processing,
        string? lastError,
        CancellationToken cancellationToken = default);
}

/// <summary>Gera zip de suporte (logs + sessão + diagnóstico), sem segredos.</summary>
public interface ISupportBundleService
{
    /// <returns>Caminho absoluto do .zip gerado.</returns>
    string CreateBundle();
}
