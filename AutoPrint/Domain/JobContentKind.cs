namespace AutoPrint.Domain;

public enum JobContentKind
{
    Text,
    Pdf,
    Image,
    EscPos
}

public static class JobContentKindExtensions
{
    public static string ToWire(this JobContentKind kind) => kind switch
    {
        JobContentKind.Text => "text",
        JobContentKind.Pdf => "pdf",
        JobContentKind.Image => "image",
        JobContentKind.EscPos => "escpos",
        _ => "text"
    };

    public static JobContentKind FromWire(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pdf" => JobContentKind.Pdf,
        "image" => JobContentKind.Image,
        "escpos" => JobContentKind.EscPos,
        _ => JobContentKind.Text
    };
}
