namespace SoftPrint.Domain;

/// <summary>Um passo do fluxo, para explicar onde o pedido está e o que aconteceu.</summary>
public sealed record ProcessingStep(
    DateTimeOffset At,
    string Stage,
    string Where,
    string Message,
    string? Detail = null,
    bool IsError = false);
