namespace SoftPrint.Domain;

public enum JobStatus
{
    Pending,
    Processing,
    Simulated,
    Sent,
    Uncertain
}

/// <summary>Atalhos que usam o catálogo ativo (configurado via .env na inicialização).</summary>
public static class JobStatusExtensions
{
    private static JobStatusTypeCatalog catalog = JobStatusTypeCatalog.Default;

    public static void UseCatalog(JobStatusTypeCatalog value) =>
        catalog = value ?? throw new ArgumentNullException(nameof(value));

    public static string ToWire(this JobStatus status) => catalog.ToWire(status);
    public static string ToDisplay(this JobStatus status) => catalog.ToDisplay(status);
    public static JobStatus FromWire(string? value) => catalog.FromWire(value);
}
