using System.Text;

namespace SoftPrint.Infrastructure.Persistence;

/// <summary>
/// Gravação e leitura dos .json de estado (fila, configurações).
/// Grava num .tmp, força o conteúdo para o disco e só então troca o arquivo: sem o Flush(true),
/// uma queda de energia logo após a troca pode deixar o arquivo cheio de zeros.
/// </summary>
public static class DurableJsonFile
{
    public static void Write(string path, string contents)
    {
        var temporary = path + ".tmp";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    /// <summary>
    /// Lê e converte o arquivo. Se estiver corrompido, renomeia para "*.corrompido-AAAAMMDD-HHMMSS"
    /// (para suporte) e devolve null — o SoftPrint abre com o padrão em vez de não abrir.
    /// </summary>
    public static T? ReadOrQuarantine<T>(string path, Func<string, T?> parse, Action<string>? onCorrupt = null)
        where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = parse(File.ReadAllText(path));
            if (value is not null) return value;
            throw new InvalidDataException("conteúdo vazio");
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            var quarantine = $"{path}.corrompido-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(path, quarantine, true); }
            catch { /* sem permissão: a próxima gravação sobrescreve */ }
            onCorrupt?.Invoke(
                $"{Path.GetFileName(path)} estava corrompido ({ex.Message}). " +
                $"Cópia guardada em {Path.GetFileName(quarantine)}; o SoftPrint começou do padrão.");
            return null;
        }
    }
}
