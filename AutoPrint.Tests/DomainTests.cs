using AutoPrint.Domain;
using Xunit;

namespace AutoPrint.Tests;

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
}
