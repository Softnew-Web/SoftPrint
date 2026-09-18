using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AutoPrint.Application;
using AutoPrint.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoPrint.Infrastructure.Printing;

/// <summary>Varre a sub-rede local em busca de portas de impressão (9100 / 515 / 631).</summary>
public sealed class NetworkPrinterDiscovery(IOptions<AutoPrintFeatureOptions> options) : INetworkPrinterDiscovery
{
    private static readonly int[] Ports = [9100, 515, 631];

    public async Task<IReadOnlyList<DiscoveredNetworkPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, DiscoveredNetworkPrinter>();
        var prefixes = GetLocalPrefixes();
        var timeoutMs = Math.Clamp(options.Value.NetworkScanTimeoutMs, 100, 3000);
        var tasks = new List<Task>();

        foreach (var prefix in prefixes)
        {
            for (var host = 1; host <= 254; host++)
            {
                var ip = $"{prefix}.{host}";
                foreach (var port in Ports)
                    tasks.Add(ProbeAsync(ip, port, timeoutMs, results, cancellationToken));
            }
        }

        foreach (var batch in tasks.Chunk(64))
            await Task.WhenAll(batch);

        return results.Values
            .OrderBy(r => r.Address)
            .ThenBy(r => r.Port)
            .ToArray();
    }

    private static async Task ProbeAsync(
        string ip, int port, int timeoutMs,
        ConcurrentDictionary<string, DiscoveredNetworkPrinter> results,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
            if (client.Connected)
            {
                var item = new DiscoveredNetworkPrinter(
                    ip, port,
                    port switch
                    {
                        9100 => "Provável impressora RAW/JetDirect",
                        515 => "Provável LPD",
                        631 => "Provável IPP/CUPS",
                        _ => "Porta de impressão aberta"
                    },
                    true);
                results.TryAdd($"{ip}:{port}", item);
            }
        }
        catch
        {
            // host offline / porta fechada
        }
    }

    private static IReadOnlyList<string> GetLocalPrefixes()
    {
        var prefixes = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = uni.Address.GetAddressBytes();
                if (bytes[0] == 127) continue;
                prefixes.Add($"{bytes[0]}.{bytes[1]}.{bytes[2]}");
            }
        }

        if (prefixes.Count == 0) prefixes.Add("192.168.0");
        return prefixes.ToArray();
    }
}
