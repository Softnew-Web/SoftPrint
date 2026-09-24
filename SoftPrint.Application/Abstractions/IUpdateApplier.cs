namespace SoftPrint.Application.Abstractions;

public interface IUpdateApplier
{
    UpdateApplyStatus Status { get; }

    /// <summary>Inicia download + instalação da versão mais nova (GitHub latest).</summary>
    bool TryStart(out string? error);

    /// <summary>
    /// Instala uma versão específica. Se <paramref name="targetVersion"/> for nulo,
    /// usa a release anterior à instalada (rollback).
    /// </summary>
    bool TryStartRollback(string? targetVersion, out string? error);

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

/// <summary>Guarda só a versão imediatamente anterior (uma cópia local), para rollback leve.</summary>
public interface IPreviousVersionStore
{
    PreviousVersionInfo? TryGet();
}

public sealed record PreviousVersionInfo(string Version, string DirectoryPath);
