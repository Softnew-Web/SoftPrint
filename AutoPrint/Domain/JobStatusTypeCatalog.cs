namespace AutoPrint.Domain;

/// <summary>Definição configurável de um tipo de status (wire + rótulo).</summary>
public sealed record JobStatusType(string Wire, string Label);

/// <summary>Catálogo dos tipos de status — valores vêm do .env / configuração.</summary>
public sealed class JobStatusTypeCatalog
{
    private readonly IReadOnlyDictionary<JobStatus, JobStatusType> _byStatus;
    private readonly IReadOnlyDictionary<string, JobStatus> _byWire;

    public JobStatusTypeCatalog(IReadOnlyDictionary<JobStatus, JobStatusType> types)
    {
        _byStatus = types;
        _byWire = types.ToDictionary(
            pair => pair.Value.Wire,
            pair => pair.Key,
            StringComparer.OrdinalIgnoreCase);
    }

    public static JobStatusTypeCatalog Default { get; } = new(CreateDefaults());

    public IReadOnlyDictionary<JobStatus, JobStatusType> All => _byStatus;

    public string ToWire(JobStatus status) =>
        _byStatus.TryGetValue(status, out var type) ? type.Wire : status.ToString().ToLowerInvariant();

    public string ToDisplay(JobStatus status) =>
        _byStatus.TryGetValue(status, out var type) ? type.Label : status.ToString();

    public JobStatus FromWire(string? value)
    {
        if (value is not null && _byWire.TryGetValue(value.Trim(), out var status))
            return status;
        throw new ArgumentOutOfRangeException(nameof(value), value, "Status de trabalho desconhecido.");
    }

    public static Dictionary<JobStatus, JobStatusType> CreateDefaults() => new()
    {
        [JobStatus.Pending] = new("pending", "Na fila"),
        [JobStatus.Processing] = new("processing", "Enviando"),
        [JobStatus.Simulated] = new("simulated", "Simulado"),
        [JobStatus.Sent] = new("sent", "Enviado ao Windows"),
        [JobStatus.Uncertain] = new("uncertain", "Conferir envio")
    };
}
