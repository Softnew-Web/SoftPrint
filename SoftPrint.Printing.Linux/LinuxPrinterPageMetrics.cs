using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Printing.Linux;

public sealed class LinuxPrinterPageMetrics(IExternalCommandRunner? runner = null) : IPrinterPageMetrics
{
    private readonly IExternalCommandRunner _runner = runner ?? ProcessCommandRunner.Instance;

    public PrinterPageMetricsInfo Read(string printerName, PrintOptions settings)
    {
        var (width, height) = settings.EffectivePaperMm();
        var margin = Math.Min(width, height) * 0.06;
        if (string.IsNullOrWhiteSpace(printerName))
            return new("cups-approximate", width, height, margin, margin, margin, margin);

        try
        {
            var options = _runner.Capture("lpoptions", "-p", printerName, "-l");
            var pageSize = options.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("PageSize/", StringComparison.OrdinalIgnoreCase));
            if (pageSize is not null && pageSize.Contains("A5", StringComparison.OrdinalIgnoreCase))
                return new("cups-lpoptions", 148, 210, margin, margin, margin, margin);
            if (pageSize is not null && pageSize.Contains("Letter", StringComparison.OrdinalIgnoreCase))
                return new("cups-lpoptions", 215.9, 279.4, margin, margin, margin, margin);
        }
        catch
        {
            /* keep approximate */
        }

        return new("cups-approximate", width, height, margin, margin, margin, margin);
    }
}
