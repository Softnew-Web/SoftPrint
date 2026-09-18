namespace SoftPrint.Domain;

public enum ImageFitMode
{
    /// <summary>Cabe inteira na página, mantém proporção (pode sobrar margem).</summary>
    Contain = 0,
    /// <summary>Preenche a página, mantém proporção (pode cortar).</summary>
    Cover = 1,
    /// <summary>Estica para preencher, pode distorcer.</summary>
    Stretch = 2,
    /// <summary>Tamanho original (ou escala %) centralizado; pode cortar se maior.</summary>
    Center = 3
}

public static class ImageFitModeExtensions
{
    public static string ToWire(this ImageFitMode mode) => mode switch
    {
        ImageFitMode.Cover => "cover",
        ImageFitMode.Stretch => "stretch",
        ImageFitMode.Center => "center",
        _ => "contain"
    };

    public static ImageFitMode FromWire(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cover" => ImageFitMode.Cover,
        "stretch" => ImageFitMode.Stretch,
        "center" => ImageFitMode.Center,
        _ => ImageFitMode.Contain
    };

    public static string ToDisplay(this ImageFitMode mode) => mode switch
    {
        ImageFitMode.Cover => "Preencher (recorta)",
        ImageFitMode.Stretch => "Esticar",
        ImageFitMode.Center => "Centralizar",
        _ => "Caber na página"
    };
}
