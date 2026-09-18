namespace SoftPrint.Application.Abstractions;

public interface IUpdateApplier
{
    UpdateApplyStatus Status { get; }

    /// <summary>Inicia download + instalação silenciosa em background. Retorna false se já estiver em andamento.</summary>
    bool TryStart(out string? error);

    UpdateApplyStatus GetStatus();
}

public sealed record UpdateApplyStatus(
    string Phase,
    int Percent,
    string Message,
    bool InProgress,
    bool Failed,
    bool Restarting,
    string? Error = null);
