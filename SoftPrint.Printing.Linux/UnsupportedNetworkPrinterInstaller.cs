using SoftPrint.Application.Abstractions;

namespace SoftPrint.Printing.Linux;

public sealed class UnsupportedNetworkPrinterInstaller : INetworkPrinterInstaller
{
    public Task<NetworkPrinterInstallResult> InstallAsync(
        string address,
        int port = 9100,
        string? displayName = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new NetworkPrinterInstallResult(
            false, "", "", "",
            "Instalação automática de impressora de rede só está disponível no Windows."));
}
