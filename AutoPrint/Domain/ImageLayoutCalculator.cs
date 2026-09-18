using System.Drawing;

namespace AutoPrint.Domain;

public static class ImageLayoutCalculator
{
    public static RectangleF ComputeDestination(
        float pageX, float pageY, float pageW, float pageH,
        float imageW, float imageH,
        ImageFitMode fit,
        int scalePercent)
    {
        scalePercent = Math.Clamp(scalePercent, 10, 200);
        var scale = scalePercent / 100f;

        return fit switch
        {
            ImageFitMode.Stretch => new RectangleF(pageX, pageY, pageW, pageH),
            ImageFitMode.Center => Center(pageX, pageY, pageW, pageH, imageW * scale, imageH * scale),
            ImageFitMode.Cover => Cover(pageX, pageY, pageW, pageH, imageW, imageH, scale),
            _ => Contain(pageX, pageY, pageW, pageH, imageW, imageH, scale)
        };
    }

    private static RectangleF Contain(float px, float py, float pw, float ph, float iw, float ih, float scale)
    {
        var ratio = Math.Min(pw / iw, ph / ih) * scale;
        var w = iw * ratio;
        var h = ih * ratio;
        return new RectangleF(px + (pw - w) / 2f, py + (ph - h) / 2f, w, h);
    }

    private static RectangleF Cover(float px, float py, float pw, float ph, float iw, float ih, float scale)
    {
        var ratio = Math.Max(pw / iw, ph / ih) * scale;
        var w = iw * ratio;
        var h = ih * ratio;
        return new RectangleF(px + (pw - w) / 2f, py + (ph - h) / 2f, w, h);
    }

    private static RectangleF Center(float px, float py, float pw, float ph, float w, float h) =>
        new(px + (pw - w) / 2f, py + (ph - h) / 2f, w, h);
}
