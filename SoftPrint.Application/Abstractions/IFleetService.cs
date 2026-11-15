namespace SoftPrint.Application.Abstractions;

/// <summary>Situação da ligação com o monitoramento Softnew (backendempresa).</summary>
public sealed record FleetStatus(
    bool Configured,
    bool TokenFromEnv,
    string TokenHint,
    string MachineId,
    string Hostname,
    string Url,
    int HeartbeatSeconds,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    bool? LastOk,
    string? LastMessage,
    string? Empresa,
    bool RealtimeConnected = false,
    string? LastCommand = null);

public sealed record FleetSendResult(bool Ok, string Message, int? StatusCode = null, int PendingCommands = 0);

/// <summary>Comando do painel para esta máquina.</summary>
public sealed record FleetCommand(long Id, string Type, string? RequestedBy);

/// <summary>Credencial para o WebSocket do hub (só a sala desta máquina).</summary>
public sealed record FleetRealtimeTicket(Uri WsUrl, string Token, string Room, DateTimeOffset ExpiresAt);

/// <summary>Atualização pedida pelo painel, cujo resultado só sai depois do reinício.</summary>
public sealed record FleetPendingUpdate(long CommandId, string FromVersion);

/// <summary>
/// Heartbeat, comandos e pacote de suporte com o backend da Softnew, usando o token da empresa.
/// Sem token configurado, nada é enviado.
/// </summary>
public interface IFleetService
{
    FleetStatus GetStatus();

    /// <summary>Grava o token (vazio apaga o token salvo; o do .env continua valendo).</summary>
    void SetToken(string? token);

    /// <summary>
    /// Troca o token como <see cref="SetToken"/>, mas antes avisa "stopping" com o token antigo:
    /// sem isso o cadastro antigo da máquina para de receber sinal e o painel acusa queda.
    /// </summary>
    Task ChangeTokenAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancelado sempre que o token muda (o WebSocket reconecta com o novo).
    /// Leia antes de pedir o ticket do tempo real: um token trocado entre as duas leituras ficaria sem aviso.
    /// </summary>
    CancellationToken TokenChanged { get; }

    /// <param name="kind">"heartbeat" ou "stopping" (SoftPrint fechando normalmente).</param>
    Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default);

    Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default);

    /// <summary>Busca (e o backend marca como entregues) os comandos pendentes.</summary>
    Task<IReadOnlyList<FleetCommand>> GetCommandsAsync(CancellationToken cancellationToken = default);

    Task<bool> SendCommandResultAsync(long commandId, bool ok, string message, CancellationToken cancellationToken = default);

    /// <summary>Null quando o servidor não tem tempo real ou a máquina ainda não foi registrada.</summary>
    Task<FleetRealtimeTicket?> GetRealtimeTicketAsync(CancellationToken cancellationToken = default);

    void SetRealtimeConnected(bool connected);

    void NoteCommand(string description);

    void RememberPendingUpdate(long commandId);

    FleetPendingUpdate? GetPendingUpdate();

    void ClearPendingUpdate();
}
