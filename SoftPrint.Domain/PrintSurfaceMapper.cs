namespace SoftPrint.Domain;

/// <summary>
/// Converte a área imprimível do papel (origem no canto da folha) para coordenadas
/// do Graphics do Windows, cuja origem é a margem física (HardMargin).
/// </summary>
public static class PrintSurfaceMapper
{
    public static LayoutRect ContentBounds(
        float printableX,
        float printableY,
        float printableW,
        float printableH,
        float hardMarginX,
        float hardMarginY,
        float pageWidth,
        float pageHeight)
    {
        hardMarginX = FiniteNonNeg(hardMarginX);
        hardMarginY = FiniteNonNeg(hardMarginY);
        pageWidth = Math.Max(1, pageWidth);
        pageHeight = Math.Max(1, pageHeight);

        var x = printableX - hardMarginX;
        var y = printableY - hardMarginY;
        var w = printableW;
        var h = printableH;

        if (w < 1 || h < 1)
        {
            x = 0;
            y = 0;
            w = Math.Max(1, pageWidth - hardMarginX);
            h = Math.Max(1, pageHeight - hardMarginY);
        }

        if (x < 0)
        {
            w += x;
            x = 0;
        }

        if (y < 0)
        {
            h += y;
            y = 0;
        }

        w = Math.Clamp(w, 1, pageWidth);
        h = Math.Clamp(h, 1, pageHeight);
        return new LayoutRect(x, y, w, h);
    }

    public static string Describe(PrintOptions settings)
    {
        var (w, h) = settings.EffectivePaperMm();
        return $"{w:0.#}×{h:0.#} mm • {settings.ImageFit.ToDisplay()} • {settings.ImageScalePercent}%";
    }

    private static float FiniteNonNeg(float value) =>
        float.IsFinite(value) && value > 0 ? value : 0;
}
