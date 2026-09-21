using System.Diagnostics;
using System.Text;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Printing;

/// <summary>Cria porta TCP/IP + fila no spooler Windows a partir de um IP descoberto.</summary>
public sealed class WindowsNetworkPrinterInstaller : INetworkPrinterInstaller
{
    public async Task<NetworkPrinterInstallResult> InstallAsync(
        string address,
        int port = 9100,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var ip = (address ?? "").Trim();
        if (ip.Length == 0 || ip.Contains('"') || ip.Contains('\'') || ip.Contains(';'))
            return new NetworkPrinterInstallResult(false, "", "", "", "Endereço IP inválido.");

        if (port is < 1 or > 65535)
            return new NetworkPrinterInstallResult(false, "", "", "", "Porta inválida.");

        var printerName = string.IsNullOrWhiteSpace(displayName)
            ? $"Impressora rede {ip}"
            : displayName.Trim();
        if (printerName.Length > 120) printerName = printerName[..120];

        var portName = $"IP_{ip}";
        var script = BuildScript(ip, port, portName, printerName);
        try
        {
            var (exit, stdout, stderr) = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            var combined = $"{stdout}\n{stderr}".Trim();
            if (exit != 0)
                return new NetworkPrinterInstallResult(false, printerName, portName, "", Truncate(combined));

            var driver = ExtractMarker(combined, "DRIVER=") ?? "";
            return new NetworkPrinterInstallResult(true, printerName, portName, driver, null);
        }
        catch (Exception ex)
        {
            return new NetworkPrinterInstallResult(false, printerName, portName, "", ex.Message);
        }
    }

    private static string BuildScript(string ip, int port, string portName, string printerName) =>
        $$"""
        $ErrorActionPreference = 'Stop'
        $ip = '{{ip}}'
        $portNumber = {{port}}
        $portName = '{{portName.Replace("'", "''")}}'
        $printerName = '{{printerName.Replace("'", "''")}}'

        if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
          Add-PrinterPort -Name $portName -PrinterHostAddress $ip -PortNumber $portNumber
        }

        $preferred = @(
          'HP LaserJet Mono PCLmS Class Driver',
          'HP LaserJet Mono PCL6 Class Driver',
          'Microsoft IPP Class Driver',
          'Generic / Text Only'
        )
        $driver = $null
        foreach ($name in $preferred) {
          if (Get-PrinterDriver -Name $name -ErrorAction SilentlyContinue) { $driver = $name; break }
        }
        if (-not $driver) {
          $driver = (Get-PrinterDriver | Where-Object { $_.Name -match 'Class Driver|Generic' } | Select-Object -First 1 -ExpandProperty Name)
        }
        if (-not $driver) { throw 'Nenhum driver de impressora adequado encontrado no Windows.' }

        if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
          Set-Printer -Name $printerName -DriverName $driver -PortName $portName
        } else {
          Add-Printer -Name $printerName -DriverName $driver -PortName $portName
        }
        Write-Output ("DRIVER=" + $driver)
        Write-Output ("OK=" + $printerName)
        """;

    private static async Task<(int Exit, string StdOut, string StdErr)> RunPowerShellAsync(
        string script, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.StandardInput.WriteAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static string? ExtractMarker(string text, string prefix)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return t[prefix.Length..].Trim();
        }
        return null;
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300];
}
