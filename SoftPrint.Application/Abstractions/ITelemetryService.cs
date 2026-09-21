using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface ITelemetryService
{
    /// <summary>Envia telemetria de falha (uncertain). Nunca lança; no-op se desabilitado.</summary>
    Task ReportPrintFailureAsync(PrintJob job, CancellationToken cancellationToken = default);
}
