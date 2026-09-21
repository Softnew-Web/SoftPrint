namespace SoftPrint.Application.Abstractions;

public interface INetworkPrinterInstaller
{
    Task<NetworkPrinterInstallResult> InstallAsync(
        string address,
        int port = 9100,
        string? displayName = null,
        CancellationToken cancellationToken = default);
}

public sealed record NetworkPrinterInstallResult(
    bool Ok,
    string PrinterName,
    string PortName,
    string DriverName,
    string? Error);
