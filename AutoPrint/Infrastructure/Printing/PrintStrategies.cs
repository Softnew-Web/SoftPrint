using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;
using AutoPrint.Application.Abstractions;
using AutoPrint.Domain;

namespace AutoPrint.Infrastructure.Printing;

public sealed class SimulationPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) => settings.Simulation;

    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken) =>
        Task.FromResult(JobStatus.Simulated);
}

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

        using var image = Image.FromFile(job.SourcePath);
        using var document = new PrintDocument();
        document.PrinterSettings.PrinterName = settings.PrinterName;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Impressora não encontrada: '{settings.PrinterName}'.");
        PrintPageSetup.Apply(document, settings);
        document.DocumentName = $"AutoPrint {job.Reference}";
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
            graphics.DrawImage(image, dest);
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

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = job.SourcePath,
            Verb = "printto",
            Arguments = $"\"{settings.PrinterName}\"",
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            UseShellExecute = true
        };

        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            start.Verb = "print";
            start.Arguments = "";
            using var fallback = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Windows não iniciou a impressão do PDF.");
            if (!fallback.WaitForExit(120_000))
                throw new InvalidOperationException("Tempo esgotado aguardando a impressão do PDF.");
        }
        else if (!process.WaitForExit(120_000))
            throw new InvalidOperationException("Tempo esgotado aguardando a impressão do PDF.");

        return Task.FromResult(JobStatus.Sent);
    }
}

public sealed class EscPosPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) =>
        !settings.Simulation && job.ContentKind == JobContentKind.EscPos;

    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken)
    {
        var payload = BuildEscPos(job.Text);
        RawPrinterHelper.SendBytes(settings.PrinterName, payload, $"AutoPrint {job.Reference}");
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
        document.DocumentName = $"AutoPrint {reference}";
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
