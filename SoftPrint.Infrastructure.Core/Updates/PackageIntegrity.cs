using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    /// <summary>Exige SHA256 — sem hash ou mismatch = falha (fail-closed).</summary>
    public static void EnsureMatches(string filePath, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new InvalidOperationException(
                "Release sem checksum SHA256. A atualização foi recusada por segurança. " +
                "Publique checksums.sha256 no GitHub Release.");

        var expected = Normalize(expectedSha256);
        if (expected.Length != 64)
            throw new InvalidOperationException("Checksum SHA256 inválido no release.");

        var actual = ComputeSha256Hex(filePath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "O arquivo baixado não passou na verificação SHA256 (pode estar corrompido). Tente atualizar de novo.");
    }

    /// <summary>
    /// No Windows: se o EXE estiver assinado, valida Authenticode.
    /// Se RequireSigned=true e não houver assinatura, falha.
    /// </summary>
    public static void EnsureAuthenticode(string exePath, bool requireSigned)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(exePath)) return;

        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile — ok em net6/net10 para Authenticode PE
            using var cert = X509Certificate.CreateFromSignedFile(exePath);
#pragma warning restore SYSLIB0057
            if (cert is null || string.IsNullOrWhiteSpace(cert.Subject))
            {
                if (requireSigned)
                    throw new InvalidOperationException(
                        $"O executável '{Path.GetFileName(exePath)}' não está assinado (Authenticode).");
                return;
            }

            // CreateFromSignedFile já prova que há assinatura embutida.
            _ = cert.GetCertHashString();
        }
        catch (CryptographicException) when (!requireSigned)
        {
            /* release sem assinatura — permitido quando RequireSignedUpdates=false */
        }
        catch (CryptographicException ex) when (requireSigned)
        {
            throw new InvalidOperationException(
                $"Falha na verificação Authenticode de '{Path.GetFileName(exePath)}': {ex.Message}", ex);
        }
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
