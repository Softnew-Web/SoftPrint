using System.Security.Cryptography;
using System.Text;

namespace SoftPrint.Infrastructure.Updates;

public static class PackageIntegrity
{
    public static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void EnsureMatches(string filePath, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return;

        var expected = Normalize(expectedSha256);
        if (expected.Length != 64)
            throw new InvalidOperationException("Checksum SHA256 inválido no release.");

        var actual = ComputeSha256Hex(filePath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "O arquivo baixado não passou na verificação SHA256 (pode estar corrompido). Tente atualizar de novo.");
    }

    /// <summary>Formato típico: "hash  nome.zip" ou "hash *nome.exe".</summary>
    public static string? FindHashForFile(string checksumsFileContent, string fileName)
    {
        if (string.IsNullOrWhiteSpace(checksumsFileContent) || string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (var raw in checksumsFileContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var hash = Normalize(parts[0]);
            var name = parts[^1].TrimStart('*');
            if (hash.Length == 64 &&
                string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                return hash;
        }

        return null;
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);
}
