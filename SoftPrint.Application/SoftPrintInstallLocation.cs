using System.Diagnostics;

namespace SoftPrint.Application;

/// <summary>
/// Onde o SoftPrint está instalado de verdade: a pasta do executável em execução.
/// O instalador aceita "só para mim" (%LocalAppData%\Programs), "todos os usuários"
/// (Program Files) ou qualquer pasta escolhida — não dá para supor um caminho fixo.
/// </summary>
public static class SoftPrintInstallLocation
{
    /// <summary>Pasta do executável, ou null se não for uma instalação do SoftPrint (ex.: "dotnet app.dll").</summary>
    public static string? TryGetCurrent()
    {
        try
        {
#if NET6_0_OR_GREATER
            var exe = Environment.ProcessPath;
#else
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
#endif
            var dir = string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
            return !string.IsNullOrWhiteSpace(dir) && HasApp(dir!) ? dir : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool HasApp(string dir) =>
        File.Exists(Path.Combine(dir, "SoftPrint.exe")) ||
        File.Exists(Path.Combine(dir, "SoftPrint.Legacy.exe"));

    /// <summary>
    /// Este processo consegue gravar na pasta? Program Files sem elevação não consegue:
    /// aí a cópia precisa rodar como administrador (UAC).
    /// </summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".softprint-write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// %LocalAppData%\SoftPrint do processo. Só o próprio usuário (ou, no serviço, só o SYSTEM)
    /// grava aqui — ao contrário de C:\Windows\Temp, que qualquer usuário pode preencher.
    /// </summary>
    public static string LocalStateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftPrint");
}
