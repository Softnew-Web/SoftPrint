using System.Text;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Printing.Linux;

public sealed class LinuxSimulationPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) => settings.Simulation;
    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken) =>
        Task.FromResult(JobStatus.Simulated);
}

public sealed class CupsPrintStrategy(IExternalCommandRunner? runner = null) : IPrintStrategy
{
    private readonly IExternalCommandRunner _runner = runner ?? ProcessCommandRunner.Instance;

    public bool CanHandle(PrintJob job, PrintOptions settings) =>
        !settings.Simulation && !TcpPrinterTarget.TryParse(settings.PrinterName, out _, out _);

    public async Task<JobStatus> ExecuteAsync(
        PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
    {
        string? temporary = null;
        var source = job.SourcePath;
        var raw = job.ContentKind == JobContentKind.EscPos;
        if (job.ContentKind == JobContentKind.Text || raw)
        {
            temporary = Path.Combine(Path.GetTempPath(), $"softprint-{job.Id:N}.{(raw ? "bin" : "txt")}");
            if (raw)
            {
                await File.WriteAllBytesAsync(temporary, EscPosPayload.FromText(job.Text), cancellationToken);
            }
            else
            {
                await File.WriteAllTextAsync(temporary, job.Text, Encoding.UTF8, cancellationToken);
            }
            source = temporary;
        }
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new InvalidOperationException("Arquivo para impressão não encontrado.");

        try
        {
            var (width, height) = settings.EffectivePaperMm();
            var args = new List<string>
            {
                "-d", settings.PrinterName,
                "-t", $"SoftPrint {job.Reference}",
            };
            if (raw)
            {
                args.AddRange(["-o", "raw"]);
            }
            else
            {
                args.AddRange([
                    "-o", $"media=Custom.{width:0.##}x{height:0.##}mm",
                    "-o", settings.ImageFit == ImageFitMode.Cover ? "print-scaling=fill" : "print-scaling=fit",
                    "-o", $"scaling={settings.ImageScalePercent}"
                ]);
            }
            args.Add(source);
            var exit = await _runner.RunAsync("lp", args, cancellationToken);
            if (exit != 0)
                throw new InvalidOperationException($"CUPS recusou o trabalho (código {exit}).");
            return JobStatus.Sent;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch { /* ignore */ }
        }
    }
}

public sealed class TcpEscPosPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) =>
        !settings.Simulation &&
        job.ContentKind == JobContentKind.EscPos &&
        TcpPrinterTarget.TryParse(settings.PrinterName, out _, out _);

    public async Task<JobStatus> ExecuteAsync(
        PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
    {
        if (!TcpPrinterTarget.TryParse(settings.PrinterName, out var host, out var port))
            throw new InvalidOperationException("Destino TCP 9100 inválido.");

        var payload = EscPosPayload.FromText(job.Text);
        using var client = new System.Net.Sockets.TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(host, port, timeout.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(payload, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        return JobStatus.Sent;
    }
}

internal static class EscPosPayload
{
    public static byte[] FromText(string text)
    {
        var body = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        var bytes = new byte[body.Length + 6];
        bytes[0] = 0x1B; bytes[1] = 0x40;
        Buffer.BlockCopy(body, 0, bytes, 2, body.Length);
        bytes[^4] = 0x0A; bytes[^3] = 0x1D; bytes[^2] = 0x56; bytes[^1] = 0x00;
        return bytes;
    }
}
