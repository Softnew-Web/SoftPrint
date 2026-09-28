namespace SoftPrint.Domain;

public sealed class InboxEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; init; } = "";
    public string PrinterName { get; init; } = "";
    public string Folder { get; init; } = "";
    public bool Enabled { get; init; }
    public bool DeleteAfterPrint { get; init; }
    public PrintJobSettings? CustomSettings { get; init; }
    public int Copies { get; init; } = 1;
    public string? WebhookUrl { get; init; }
}
