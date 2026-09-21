namespace SoftPrint.Domain;

/// <summary>Papel anunciado pelo driver (Windows PaperSizes / CUPS PageSize).</summary>
public readonly record struct PrinterPaperCandidate(
    string Name,
    double WidthMm,
    double HeightMm,
    int RawKind);

/// <summary>Papel que o SoftPrint deve pedir ao driver para honrar a configuração.</summary>
public sealed record PrinterPaperChoice(
    string Name,
    double WidthMm,
    double HeightMm,
    int? RawKind,
    bool ExactNativeMatch,
    bool UseDriverLandscape,
    bool WidthOnlyNativeMatch);

public static class PrinterPaperMatcher
{
    /// <summary>
    /// Prefere um papel nativo com a mesma largura e altura. Não usa o rolo térmico
    /// “comprido” só porque a largura bate — isso faria a senha sair em metros de papel.
    /// Se não houver tamanho nativo, devolve o tamanho configurado (personalizado),
    /// reusando o RawKind de um papel da mesma largura quando existir (formulário térmico).
    /// </summary>
    public static PrinterPaperChoice Resolve(
        IReadOnlyList<PrinterPaperCandidate> native,
        double widthMm,
        double heightMm,
        PaperSizeKind kind,
        double exactToleranceMm = 2.5)
    {
        PrinterPaperCandidate? exact = null;
        var exactSwapped = false;
        PrinterPaperCandidate? widthOnly = null;

        foreach (var paper in native)
        {
            if (Near(paper.WidthMm, widthMm, exactToleranceMm) &&
                Near(paper.HeightMm, heightMm, exactToleranceMm))
            {
                exact = paper;
                exactSwapped = false;
                break;
            }

            if (Near(paper.WidthMm, heightMm, exactToleranceMm) &&
                Near(paper.HeightMm, widthMm, exactToleranceMm))
            {
                exact = paper;
                exactSwapped = true;
                break;
            }

            if (widthOnly is null && Near(paper.WidthMm, widthMm, exactToleranceMm))
                widthOnly = paper;
        }

        if (exact is { } hit)
        {
            return new PrinterPaperChoice(
                hit.Name,
                hit.WidthMm,
                hit.HeightMm,
                hit.RawKind,
                ExactNativeMatch: true,
                UseDriverLandscape: exactSwapped,
                WidthOnlyNativeMatch: false);
        }

        var isReceipt = kind is PaperSizeKind.Receipt58 or PaperSizeKind.Receipt80;
        return new PrinterPaperChoice(
            $"SoftPrint {widthMm:0.#}x{heightMm:0.#}mm",
            widthMm,
            heightMm,
            isReceipt ? widthOnly?.RawKind : null,
            ExactNativeMatch: false,
            UseDriverLandscape: false,
            WidthOnlyNativeMatch: widthOnly is not null);
    }

    public static bool SizeMatches(
        double actualWidthMm,
        double actualHeightMm,
        double requestedWidthMm,
        double requestedHeightMm,
        double toleranceMm = 3)
    {
        return (Near(actualWidthMm, requestedWidthMm, toleranceMm) &&
                Near(actualHeightMm, requestedHeightMm, toleranceMm))
               || (Near(actualWidthMm, requestedHeightMm, toleranceMm) &&
                   Near(actualHeightMm, requestedWidthMm, toleranceMm));
    }

    private static bool Near(double a, double b, double tolerance) =>
        Math.Abs(a - b) <= tolerance;
}
