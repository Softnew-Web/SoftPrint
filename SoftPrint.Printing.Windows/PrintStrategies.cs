using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using PDFtoImage;
using SkiaSharp;

namespace SoftPrint.Infrastructure.Printing;

public sealed class WindowsPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) =>
        !settings.Simulation && job.ContentKind == JobContentKind.Text;

    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
    {
        TextPrintEngine.Print(job.Text, settings, job.Reference);
        return Task.FromResult(JobStatus.Sent);
    }
}

public sealed class ImagePrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) =>
        !settings.Simulation && job.ContentKind == JobContentKind.Image;

    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.SourcePath) || !File.Exists(job.SourcePath))
            throw new InvalidOperationException("Arquivo de imagem não encontrado.");

        using var skiaImage = SKBitmap.Decode(job.SourcePath)
            ?? throw new InvalidOperationException("Formato de imagem inválido ou não suportado.");
        using var image = SkiaGdiBridge.ToBitmap(skiaImage);
        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = settings.PrinterName;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Impressora não encontrada: '{settings.PrinterName}'.");
        PrintPageSetup.Apply(document, settings);
        document.DocumentName = $"SoftPrint {job.Reference}";
        document.PrintController = new StandardPrintController();
        document.PrintPage += (_, page) =>
        {
            var graphics = page.Graphics ?? throw new InvalidOperationException("Impressora sem área gráfica.");
            var area = page.MarginBounds;
            var dest = ImageLayoutCalculator.ComputeDestination(
                area.X, area.Y, area.Width, area.Height,
                image.Width, image.Height,
                settings.ImageFit,
                settings.ImageScalePercent);
            var saved = graphics.Save();
            graphics.SetClip(area);
            graphics.DrawImage(image, new RectangleF(dest.X, dest.Y, dest.Width, dest.Height));
            graphics.Restore(saved);
            page.HasMorePages = false;
        };
        document.Print();
        return Task.FromResult(JobStatus.Sent);
    }
}

public sealed class PdfPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) =>
        !settings.Simulation && job.ContentKind == JobContentKind.Pdf;

    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.SourcePath) || !File.Exists(job.SourcePath))
            throw new InvalidOperationException("Arquivo PDF não encontrado.");

        using var stream = File.OpenRead(job.SourcePath);
        var renderOptions = new RenderOptions(
            Dpi: 300,
            WithAnnotations: true,
            WithFormFill: true,
            BackgroundColor: SKColors.White);
        using var pages = Conversion.ToImages(stream, leaveOpen: true, options: renderOptions).GetEnumerator();

        bool hasPage;
        try { hasPage = pages.MoveNext(); }
        catch (Exception ex) { throw new InvalidOperationException($"PDF inválido, protegido ou não suportado: {ex.Message}", ex); }
        if (!hasPage)
            throw new InvalidOperationException("O PDF não possui páginas imprimíveis.");

        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = settings.PrinterName;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Impressora não encontrada: '{settings.PrinterName}'.");
        PrintPageSetup.Apply(document, settings);
        document.DocumentName = $"SoftPrint {job.Reference}";
        document.PrintController = new StandardPrintController();
        document.PrintPage += (_, page) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rendered = pages.Current;
            using var image = SkiaGdiBridge.ToBitmap(rendered);
            var area = page.MarginBounds;
            var dest = ImageLayoutCalculator.ComputeDestination(
                area.X, area.Y, area.Width, area.Height,
                image.Width, image.Height, settings.ImageFit, settings.ImageScalePercent);
            var graphics = page.Graphics ?? throw new InvalidOperationException("Impressora sem área gráfica.");
            var saved = graphics.Save();
            graphics.SetClip(area);
            graphics.DrawImage(image, new RectangleF(dest.X, dest.Y, dest.Width, dest.Height));
            graphics.Restore(saved);
            page.HasMorePages = pages.MoveNext();
        };
        document.Print();

        return Task.FromResult(JobStatus.Sent);
    }
}

internal static class SkiaGdiBridge
{
    public static Bitmap ToBitmap(SKBitmap source)
    {
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Não foi possível converter a página renderizada.");
        using var stream = encoded.AsStream();
        using var temporary = new Bitmap(stream);
        return new Bitmap(temporary);
    }
}

public sealed class EscPosPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) =>
        !settings.Simulation && job.ContentKind == JobContentKind.EscPos;

    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
    {
        var payload = BuildEscPos(job.Text);
        RawPrinterHelper.SendBytes(settings.PrinterName, payload, $"SoftPrint {job.Reference}");
        return Task.FromResult(JobStatus.Sent);
    }

    private static byte[] BuildEscPos(string text)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(0x1B); stream.WriteByte(0x40);
        var body = Encoding.GetEncoding(850).GetBytes(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        stream.Write(body);
        stream.WriteByte(0x0A);
        stream.WriteByte(0x1D); stream.WriteByte(0x56); stream.WriteByte(0x00);
        return stream.ToArray();
    }
}

internal static class PrintPageSetup
{
    public static void Apply(PrintDocument document, PrintOptions settings)
    {
        var (w, h) = PaperSizeCatalog.ToHundredthsInch(
            settings.PaperSize,
            settings.PaperWidthMm,
            settings.PaperHeightMm,
            settings.PaperLandscape);

        var name = settings.PaperSize == PaperSizeKind.Custom
            ? $"Custom {settings.PaperWidthMm:0.#}x{settings.PaperHeightMm:0.#}mm"
            : settings.PaperSize.ToDisplay();

        document.DefaultPageSettings.PaperSize = new PaperSize(name, w, h);
        document.DefaultPageSettings.Landscape = false; // rotação já embutida em w×h
    }
}

public sealed class WindowsPrinterPageMetrics : IPrinterPageMetrics
{
    public PrinterPageMetricsInfo Read(string printerName, PrintOptions settings)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return Fallback(settings);

        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid)
            return Fallback(settings);

        PrintPageSetup.Apply(document, settings);
        var page = document.DefaultPageSettings;
        var bounds = page.Bounds;
        var requested = new RectangleF(
            page.Margins.Left,
            page.Margins.Top,
            Math.Max(1, bounds.Width - page.Margins.Left - page.Margins.Right),
            Math.Max(1, bounds.Height - page.Margins.Top - page.Margins.Bottom));
        var printable = RectangleF.Intersect(requested, page.PrintableArea);
        if (printable.Width <= 0 || printable.Height <= 0)
            return Fallback(settings);

        return new PrinterPageMetricsInfo(
            "driver", HiToMm(bounds.Width), HiToMm(bounds.Height),
            HiToMm(printable.Left), HiToMm(printable.Top),
            HiToMm(bounds.Width - printable.Right), HiToMm(bounds.Height - printable.Bottom));
    }

    private static PrinterPageMetricsInfo Fallback(PrintOptions settings)
    {
        var (width, height) = settings.EffectivePaperMm();
        var margin = Math.Min(width, height) * 0.06;
        return new PrinterPageMetricsInfo(
            "approximate", width, height, margin, margin, margin, margin);
    }

    private static double HiToMm(float value) => Math.Round(value * 25.4 / 100.0, 2);
}

internal static class TextPrintEngine
{
    public static void Print(string text, PrintOptions settings, string reference)
    {
        var printer = settings.PrinterName;
        if (string.IsNullOrWhiteSpace(printer))
            throw new InvalidOperationException("Configure o nome exato da impressora.");

        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = printer;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Impressora não encontrada no Windows: '{printer}'.");

        PrintPageSetup.Apply(document, settings);
        document.DocumentName = $"SoftPrint {reference}";
        document.PrintController = new StandardPrintController();
        using var font = new Font("Consolas", 10);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.LineLimit };
        var remaining = text;
        var pageNumber = 0;
        document.PrintPage += (_, page) =>
        {
            pageNumber++;
            var graphics = page.Graphics
                ?? throw new InvalidOperationException("Impressora sem área gráfica (driver não retornou Graphics).");
            var area = page.MarginBounds;
            if (area.Width <= 0 || area.Height <= 0)
                throw new InvalidOperationException($"Área de impressão inválida na página {pageNumber}.");
            graphics.MeasureString(remaining, font, area.Size, format, out var count, out int _);
            if (count <= 0)
                throw new InvalidOperationException($"Não foi possível ajustar o texto ao papel na página {pageNumber}.");
            graphics.DrawString(remaining[..count], font, Brushes.Black, area, format);
            remaining = remaining[count..];
            page.HasMorePages = remaining.Length > 0;
        };
        document.Print();
    }
}

internal static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private class DocInfoA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName = "";
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DocInfoA di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static void SendBytes(string printerName, byte[] bytes, string documentName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new InvalidOperationException("Configure a impressora térmica (ESC/POS).");
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            throw new InvalidOperationException($"Não foi possível abrir a impressora '{printerName}'.");

        try
        {
            var di = new DocInfoA { pDocName = documentName, pDataType = "RAW" };
            if (!StartDocPrinter(handle, 1, di))
                throw new InvalidOperationException("StartDocPrinter falhou.");
            try
            {
                if (!StartPagePrinter(handle))
                    throw new InvalidOperationException("StartPagePrinter falhou.");
                var ptr = Marshal.AllocCoTaskMem(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    if (!WritePrinter(handle, ptr, bytes.Length, out _))
                        throw new InvalidOperationException("WritePrinter falhou ao enviar ESC/POS.");
                }
                finally { Marshal.FreeCoTaskMem(ptr); }
                EndPagePrinter(handle);
            }
            finally { EndDocPrinter(handle); }
        }
        finally { ClosePrinter(handle); }
    }
}
