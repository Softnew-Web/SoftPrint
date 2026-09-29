using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Printing;

/// <summary>Varre a sub-rede local em busca de portas de impressão (9100 / 515 / 631).</summary>
public sealed class NetworkPrinterDiscovery(IOptions<SoftPrintFeatureOptions> options) : INetworkPrinterDiscovery
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

#if NET6_0_OR_GREATER
        foreach (var batch in tasks.Chunk(64))
            await Task.WhenAll(batch);
#else
        foreach (var batch in ChunkFallback(tasks, 64))
            await Task.WhenAll(batch);
#endif

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
#if NET5_0_OR_GREATER
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
#else
            // netcoreapp3.1: ConnectAsync sem CancellationToken — usar Task.WhenAny com delay
            cancellationToken.ThrowIfCancellationRequested();
            var connectTask = client.ConnectAsync(IPAddress.Parse(ip), port);
            var delayTask = Task.Delay(timeoutMs, cancellationToken);
            var completed = await Task.WhenAny(connectTask, delayTask);
            if (completed != connectTask || !connectTask.IsCompletedSuccessfully)
                return; // timeout ou cancelamento
            await connectTask; // propaga exceção de connect se houver
#endif
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

#if !NET6_0_OR_GREATER
    private static IEnumerable<IEnumerable<T>> ChunkFallback<T>(IEnumerable<T> source, int size)
    {
        var list = new List<T>();
        foreach (var item in source)
        {
            list.Add(item);
            if (list.Count == size) { yield return list; list = new List<T>(); }
        }
        if (list.Count > 0) yield return list;
    }
#endif

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
