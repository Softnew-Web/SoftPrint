using SoftPrint.Domain;
using PDFtoImage;
using System.Text;
using Xunit;

namespace SoftPrint.Tests;

public sealed class DomainTests
{
    [Theory]
    [InlineData("arquivo.pdf", JobContentKind.Pdf)]
    [InlineData("foto.JPG", JobContentKind.Image)]
    [InlineData("scan.webp", JobContentKind.Image)]
    public void InboxFileRules_AcceptsSupportedFiles(string path, JobContentKind expected)
    {
        Assert.True(InboxFileRules.TryGetContentKind(path, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void InboxFileRules_RejectsUnsupportedFiles() =>
        Assert.False(InboxFileRules.TryGetContentKind("notas.txt", out _));

    [Fact]
    public void PaperSize_ResolvesLandscapeA4()
    {
        var options = new PrintOptions { PaperSize = PaperSizeKind.A4, PaperLandscape = true };
        var (width, height) = options.EffectivePaperMm();
        Assert.Equal(297, width);
        Assert.Equal(210, height);
    }

    [Theory]
    [InlineData(58, 200, PaperSizeKind.Receipt58)]
    [InlineData(80, 297, PaperSizeKind.Receipt80)]
    [InlineData(58.5, 400, PaperSizeKind.Receipt58)]
    [InlineData(100, 150, PaperSizeKind.Photo4x6)]
    [InlineData(90, 140, PaperSizeKind.Custom)]
    public void PaperSize_MatchFromMillimeters(double w, double h, PaperSizeKind expected) =>
        Assert.Equal(expected, PaperSizeCatalog.MatchFromMillimeters(w, h));

    [Fact]
    public void PaperSize_ReceiptPresetsHaveExpectedMm()
    {
        Assert.Equal((58, 200), PaperSizeCatalog.GetMillimeters(PaperSizeKind.Receipt58));
        Assert.Equal((80, 297), PaperSizeCatalog.GetMillimeters(PaperSizeKind.Receipt80));
        Assert.Equal(PaperSizeKind.Receipt80, PaperSizeCatalog.FromWire("cupom80"));
    }

    [Fact]
    public void PrintSurfaceMapper_UsesPrintableAreaMinusHardMargin()
    {
        var area = PrintSurfaceMapper.ContentBounds(
            printableX: 25, printableY: 25, printableW: 777, printableH: 1050,
            hardMarginX: 25, hardMarginY: 25,
            pageWidth: 827, pageHeight: 1169);
        Assert.Equal(0, area.X, 3);
        Assert.Equal(0, area.Y, 3);
        Assert.Equal(777, area.Width, 3);
        Assert.Equal(1050, area.Height, 3);
    }

    [Fact]
    public void PrinterPaperMatcher_KeepsReceiptHeightInsteadOfLongRoll()
    {
        var native = new[]
        {
            new PrinterPaperCandidate("A4", 210, 297, 9),
            new PrinterPaperCandidate("Roll 80 x 3276", 80, 3276, 256),
        };

        var choice = PrinterPaperMatcher.Resolve(native, 80, 297, PaperSizeKind.Receipt80);

        Assert.False(choice.ExactNativeMatch);
        Assert.Equal(80, choice.WidthMm);
        Assert.Equal(297, choice.HeightMm);
        Assert.True(choice.WidthOnlyNativeMatch);
        Assert.Equal(256, choice.RawKind);
    }

    [Fact]
    public void PrinterPaperMatcher_UsesNativeA4AndLandscapeWhenSwapped()
    {
        var native = new[] { new PrinterPaperCandidate("A4", 210, 297, 9) };
        var portrait = PrinterPaperMatcher.Resolve(native, 210, 297, PaperSizeKind.A4);
        var landscape = PrinterPaperMatcher.Resolve(native, 297, 210, PaperSizeKind.A4);

        Assert.True(portrait.ExactNativeMatch);
        Assert.False(portrait.UseDriverLandscape);
        Assert.True(landscape.ExactNativeMatch);
        Assert.True(landscape.UseDriverLandscape);
    }

    [Fact]
    public void PrintLayout_DescribeIncludesPaperFitAndScale()
    {
        var options = new PrintOptions
        {
            PaperSize = PaperSizeKind.Receipt80,
            ImageFit = ImageFitMode.Contain,
            ImageScalePercent = 100
        };
        Assert.Equal("80×297 mm • Caber na página • 100%", PrintSurfaceMapper.Describe(options));
    }

    [Fact]
    public void PrintOptions_ClampsCustomDimensionsAndScale()
    {
        var updated = new PrintOptions().WithUpdate(
            "", true, false, ImageFitMode.Contain, 999,
            PaperSizeKind.Custom, 5, 2000, false, "", false, false);

        Assert.Equal(200, updated.ImageScalePercent);
        Assert.Equal(20, updated.PaperWidthMm);
        Assert.Equal(1200, updated.PaperHeightMm);
    }

    [Theory]
    [InlineData(ImageFitMode.Contain, 0, 25, 100, 50)]
    [InlineData(ImageFitMode.Cover, -50, 0, 200, 100)]
    [InlineData(ImageFitMode.Stretch, 0, 0, 100, 100)]
    public void ImageLayout_ComputesExpectedRectangle(
        ImageFitMode fit, float x, float y, float width, float height)
    {
        var result = ImageLayoutCalculator.ComputeDestination(0, 0, 100, 100, 200, 100, fit, 100);
        Assert.Equal(x, result.X, 3);
        Assert.Equal(y, result.Y, 3);
        Assert.Equal(width, result.Width, 3);
        Assert.Equal(height, result.Height, 3);
    }

    [Fact]
    public void InboxReference_IsSafeAndBounded()
    {
        var path = Path.Combine(Path.GetTempPath(), "arquivo com espaços.pdf");
        var reference = InboxFileRules.BuildReference(path);
        Assert.StartsWith("inbox-arquivocomespaços-", reference);
        Assert.True(reference.Length <= 120);
    }

    [Fact]
    public void Pdfium_RendersEveryPageWithoutPrinting()
    {
        var pdf = BuildMinimalPdf();
        var pages = Conversion.ToImages(pdf, options: new RenderOptions(Dpi: 72)).ToArray();
        try
        {
            Assert.Equal(2, pages.Length);
            Assert.All(pages, page =>
            {
                Assert.True(page.Width > 0);
                Assert.True(page.Height > 0);
            });
        }
        finally
        {
            foreach (var page in pages) page.Dispose();
        }
    }

    private static byte[] BuildMinimalPdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 6 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    [Fact]
    public void SoftPrintVersion_MatchesVersionFile()
    {
        var root = FindRepoRoot();
        var fileVersion = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
        Assert.Equal(SoftPrintVersion.Current, fileVersion);
    }

    [Theory]
    [InlineData("v1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("2.0.0-beta", "1.9.9", true)]
    public void SoftPrintVersionCompare_DetectsNewer(string latest, string current, bool expected) =>
        Assert.Equal(expected, SoftPrintVersionCompare.IsNewer(latest, current));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VERSION")) &&
                File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
