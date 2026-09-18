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
        var reference = InboxFileRules.BuildReference(@"C:\temp\arquivo com espaços.pdf");
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
}
