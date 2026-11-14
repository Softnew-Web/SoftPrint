using SoftPrint.Api.Contracts;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Api.Endpoints;

/// <summary>
/// Lógica comum das rotas do monitoramento Softnew, usada pelo host atual (minimal API)
/// e pelo host Legacy (controllers, Windows 7).
/// </summary>
public static class FleetApi
{
    public const string KeepToken = "********";

    public static async Task<object> SaveTokenAsync(IFleetService fleet, FleetTokenRequest? request)
    {
        var token = request?.Token?.Trim() ?? "";
        if (token != KeepToken)
            await fleet.ChangeTokenAsync(token).ConfigureAwait(false);

        FleetSendResult? result = null;
        if (fleet.GetStatus().Configured)
            result = await fleet.SendHeartbeatAsync().ConfigureAwait(false);

        return new { saved = true, status = fleet.GetStatus(), result };
    }

    public static async Task<object> TestAsync(IFleetService fleet)
    {
        var result = await fleet.SendHeartbeatAsync().ConfigureAwait(false);
        return new { status = fleet.GetStatus(), result };
    }

    /// <summary>Gera o pacote de suporte e manda ao backend da Softnew.</summary>
    public static async Task<(bool ok, object body)> SendSupportBundleAsync(
        ISupportBundleService support,
        IFleetService fleet,
        SupportSendRequest? request)
    {
        if (!fleet.GetStatus().Configured)
            return (false, new { ok = false, error = "Configure o token do monitoramento Softnew antes de enviar." });

        string path;
        try
        {
            path = support.CreateBundle();
        }
        catch (Exception ex)
        {
            return (false, new { ok = false, error = "Falha ao gerar o pacote: " + ex.Message });
        }

        var result = await fleet.UploadSupportPackageAsync(path, request?.Note).ConfigureAwait(false);
        var body = new
        {
            ok = result.Ok,
            message = result.Ok ? result.Message : null,
            error = result.Ok ? null : result.Message,
            path,
            fileName = Path.GetFileName(path)
        };
        return (result.Ok, body);
    }
}
