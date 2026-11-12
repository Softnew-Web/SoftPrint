using System.Diagnostics;
using SoftPrint.Api.Contracts;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Api;

/// <summary>
/// Botão "Desinstalar" do painel (host moderno e Legacy): monta o .cmd que espera o SoftPrint
/// fechar, limpa o que o usuário escolheu e chama o unins000.exe da pasta onde o app está.
/// </summary>
public static class UninstallLauncher
{
    /// <summary>Inicia a desinstalação. FileNotFoundException se não houver desinstalador.</summary>
    public static void Start(UninstallRequest opts, IAppPaths paths)
    {
        var installDir = SoftPrintInstallLocation.TryGetCurrent();
        var uninsPath = installDir is null ? null : Path.Combine(installDir, "unins000.exe");
        if (uninsPath is null || !File.Exists(uninsPath))
            throw new FileNotFoundException(
                "Desinstalador não encontrado. Talvez o SoftPrint não tenha sido instalado via Inno Setup.");

        var configRoot = paths.ConfigRoot;
        var dataRoot = paths.DataRoot;
        var stateRoot = SoftPrintInstallLocation.LocalStateRoot;
#if NET5_0_OR_GREATER
        var pid = Environment.ProcessId;
#else
        var pid = Process.GetCurrentProcess().Id;
#endif
        // Fora do Temp compartilhado (no serviço seria C:\Windows\Temp, gravável por qualquer usuário)
        // e fora da pasta de configuração, que o próprio script pode apagar enquanto roda.
        var scriptPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoftPrint-uninstall.cmd");
        var errorLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "softprint-uninstall-log.txt");

        static string Rd(string dir) => $"if exist \"{dir}\" rmdir /s /q \"{dir}\"";
        static string Del(string file) => $"if exist \"{file}\" del /f /q \"{file}\"";

        var lines = new List<string>
        {
            "@echo off",
            "setlocal",
            ":waitpid",
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto waitpid",
            ")",
            "taskkill /F /IM SoftPrint.exe >nul 2>nul",
            "taskkill /F /IM SoftPrint.Legacy.exe >nul 2>nul",
            "timeout /t 1 /nobreak >nul",
            // Sempre removidos — não fazem sentido após desinstalar.
            Rd(Path.Combine(stateRoot, "previous")),
            Rd(Path.Combine(stateRoot, "update-work")),
            Rd(Path.Combine(configRoot, "backups"))
        };
        foreach (var f in new[] { "pending-update.json", "update-history.json", "last-update-error.txt", "update-script-error.txt" })
            lines.Add(Del(Path.Combine(stateRoot, f)));

        if (!opts.KeepWebView)
            lines.Add(Rd(Path.Combine(stateRoot, "WebView2")));

        if (!opts.KeepLogs)
        {
            lines.Add(Rd(Path.Combine(dataRoot, "logs")));
            lines.Add(Rd(Path.Combine(configRoot, "logs")));
        }

        if (!opts.KeepJobs)
            lines.Add(Del(Path.Combine(dataRoot, "jobs.json")));

        if (!opts.KeepConfig)
            lines.Add(Rd(configRoot));
        else if (!opts.KeepJobs && !opts.KeepLogs)
        {
            // data/ pode ter ficado vazia — limpa se for o caso.
            lines.Add($"if exist \"{dataRoot}\" rd \"{dataRoot}\" 2>nul");
        }

        if (opts.SaveInstaller)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var destExe = Path.Combine(desktop, "SoftPrint-Setup.exe");
            const string downloadUrl = "https://github.com/Softnew-Web/SoftPrint/releases/latest/download/SoftPrint-Setup.exe";
            lines.Add("echo Baixando instalador SoftPrint para a Area de Trabalho...");
            lines.Add($"powershell -NoProfile -NonInteractive -Command \"Invoke-WebRequest -Uri '{downloadUrl}' -OutFile '{PsQuote(destExe)}' -UseBasicParsing\" >nul 2>nul");
            lines.Add($"if not exist \"{destExe}\" echo Falha no download do instalador>> \"{errorLog}\"");
        }

        lines.Add($"\"{uninsPath}\" /VERYSILENT /NORESTART /SUPPRESSMSGBOXES");
        lines.Add("endlocal");
        // Apaga o próprio .cmd ao terminar.
        lines.Add("(goto) 2>nul & del \"%~f0\"");

        File.WriteAllLines(scriptPath, lines);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)!
        });
    }

    /// <summary>Texto dentro de aspas simples do PowerShell (inclui as aspas tipográficas, que ele também aceita).</summary>
    public static string PsQuote(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value ?? "", "['\u2018\u2019\u201A\u201B]", "$0$0");
}
