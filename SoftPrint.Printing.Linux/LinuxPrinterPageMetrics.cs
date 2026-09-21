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

        var defaults = ReadDefaultPaper(printerName);
        if (defaults is not null)
            return new("cups-lpoptions", defaults.WidthMm, defaults.HeightMm, margin, margin, margin, margin);

        return new("cups-approximate", width, height, margin, margin, margin, margin);
    }

    public PrinterDefaultPaperInfo? ReadDefaultPaper(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return null;

        try
        {
            var options = _runner.Capture("lpoptions", "-p", printerName, "-l");
            var pageSizeLine = options.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("PageSize/", StringComparison.OrdinalIgnoreCase));
            if (pageSizeLine is null)
                return null;

            var selected = ExtractSelectedOption(pageSizeLine);
            if (string.IsNullOrWhiteSpace(selected))
                return null;

            var (kind, w, h) = MapCupsPageSize(selected);
            return new PrinterDefaultPaperInfo(
                "cups-lpoptions", w, h, false, selected, kind.ToWire());
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractSelectedOption(string line)
    {
        // Ex.: PageSize/Media Size: *A4 Letter Custom.58x200mm
        var colon = line.IndexOf(':');
        var body = colon >= 0 ? line[(colon + 1)..] : line;
        foreach (var token in body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('*'))
                return token.TrimStart('*');
        }
        return null;
    }

    private static (PaperSizeKind Kind, double WidthMm, double HeightMm) MapCupsPageSize(string name)
    {
        var n = name.Trim();
        if (n.Equals("A4", StringComparison.OrdinalIgnoreCase))
            return (PaperSizeKind.A4, 210, 297);
        if (n.Equals("A5", StringComparison.OrdinalIgnoreCase))
            return (PaperSizeKind.A5, 148, 210);
        if (n.Equals("Letter", StringComparison.OrdinalIgnoreCase))
            return (PaperSizeKind.Letter, 215.9, 279.4);
        if (n.Equals("Legal", StringComparison.OrdinalIgnoreCase))
            return (PaperSizeKind.Legal, 215.9, 355.6);
        if (n.Contains("4x6", StringComparison.OrdinalIgnoreCase))
            return (PaperSizeKind.Photo4x6, 101.6, 152.4);

        // Custom.58x200mm / w58h200 / 80x297mm
        var custom = System.Text.RegularExpressions.Regex.Match(
            n, @"(?:Custom\.)?(\d+(?:\.\d+)?)\s*[xX×]\s*(\d+(?:\.\d+)?)\s*(mm)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (custom.Success)
        {
            var w = double.Parse(custom.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var h = double.Parse(custom.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var kind = PaperSizeCatalog.MatchFromMillimeters(w, h);
            var (pw, ph) = PaperSizeCatalog.GetMillimeters(kind, w, h);
            return (kind, pw, ph);
        }

        if (n.Contains("58", StringComparison.OrdinalIgnoreCase))
            return (PaperSizeKind.Receipt58, 58, 200);
        if (n.Contains("80", StringComparison.OrdinalIgnoreCase))
            return (PaperSizeKind.Receipt80, 80, 297);

        return (PaperSizeKind.A4, 210, 297);
    }
}
