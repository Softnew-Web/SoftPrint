using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Infrastructure.Updates;

/// <summary>
/// Mantém no máximo uma cópia local da instalação anterior
/// (%LocalAppData%\SoftPrint\previous) — sem acumular várias versões.
/// </summary>
public sealed class LocalPreviousVersionStore : IPreviousVersionStore
{
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "SoftPrint");

    public static string PreviousDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftPrint",
        "previous");

    public static string VersionMarkerPath => Path.Combine(PreviousDirectory, "version.txt");

    public PreviousVersionInfo? TryGet()
    {
        try
        {
            if (!Directory.Exists(PreviousDirectory) || !HasApp(PreviousDirectory))
                return null;
            if (!File.Exists(VersionMarkerPath))
                return null;
            var version = SoftPrintVersionCompare.Normalize(File.ReadAllText(VersionMarkerPath));
            if (!SoftPrintVersionCompare.IsParseable(version))
                return null;
            // Não oferece “voltar” para a mesma versão já em execução.
            if (string.Equals(version, SoftPrintVersionCompare.Normalize(SoftPrintVersion.Current),
                    StringComparison.OrdinalIgnoreCase))
                return null;
            return new PreviousVersionInfo(version, PreviousDirectory);
        }
        catch
        {
            return null;
        }
    }

    internal static bool HasApp(string dir) =>
        File.Exists(Path.Combine(dir, "SoftPrint.exe")) ||
        File.Exists(Path.Combine(dir, "SoftPrint.Legacy.exe"));
}

/// <summary>
/// Persiste falha de atualização para avisar no próximo arranque
/// (o script .cmd e o applier escrevem; a UI consome e limpa).
/// </summary>
public static class UpdateFailureNotice
{
    public static string TempErrorPath =>
        Path.Combine(Path.GetTempPath(), "softprint-update-error.txt");

    public static string PersistentErrorPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftPrint",
        "last-update-error.txt");

    public static void Record(string message)
    {
        var text = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message.Trim()}{Environment.NewLine}";
        try { File.AppendAllText(TempErrorPath, text); }
        catch { /* ignore */ }

        try
        {
            var dir = Path.GetDirectoryName(PersistentErrorPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(PersistentErrorPath, text);
        }
        catch { /* ignore */ }
    }

    /// <summary>Lê e remove avisos pendentes. Retorna null se não houver.</summary>
    public static string? TryConsume()
    {
        var chunks = new List<string>();
        foreach (var path in new[] { PersistentErrorPath, TempErrorPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var body = File.ReadAllText(path).Trim();
                if (body.Length > 0) chunks.Add(body);
                File.Delete(path);
            }
            catch { /* ignore */ }
        }

        if (chunks.Count == 0) return null;
        return string.Join(Environment.NewLine + Environment.NewLine, chunks.Distinct());
    }
}
