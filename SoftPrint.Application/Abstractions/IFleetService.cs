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
    string? Empresa);

public sealed record FleetSendResult(bool Ok, string Message, int? StatusCode = null);

/// <summary>
/// Heartbeat e envio de pacote de suporte para o backend da Softnew, com o token da empresa.
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

    /// <param name="kind">"heartbeat" ou "stopping" (SoftPrint fechando normalmente).</param>
    Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default);

    Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default);
}
