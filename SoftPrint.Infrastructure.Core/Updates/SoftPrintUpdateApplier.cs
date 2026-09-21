using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Updates;

public sealed class SoftPrintUpdateApplier : IUpdateApplier
{
    private readonly IUpdateChecker _checker;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SoftPrintFeatureOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SoftPrintUpdateApplier> _logger;
    private readonly object _gate = new();
    private UpdateApplyStatus _status = Idle();
    private int _running;

    public SoftPrintUpdateApplier(
        IUpdateChecker checker,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SoftPrintFeatureOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<SoftPrintUpdateApplier> logger)
    {
        _checker = checker;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    public UpdateApplyStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public UpdateApplyStatus GetStatus() => Status;

    public bool TryStart(out string? error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Atualização automática disponível apenas no Windows. Use o pacote Linux.";
            return false;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            error = "Uma atualização já está em andamento.";
            return false;
        }

        Set(new UpdateApplyStatus("starting", 1, "Preparando atualização…", true, false, false));
        _ = Task.Run(RunAsync);
        error = null;
        return true;
    }

    private async Task RunAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SoftPrintUpdate");
        var scriptPath = Path.Combine(tempDir, "apply-update.cmd");

        try
        {
            Directory.CreateDirectory(tempDir);
            Set(new UpdateApplyStatus("checking", 5, "Consultando versão no GitHub…", true, false, false));

            _checker.InvalidateCache();
            var check = await _checker.CheckAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(check.Error) && !check.UpdateAvailable)
                throw new InvalidOperationException(check.Error);

            if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
                throw new InvalidOperationException("Nenhuma atualização disponível para instalar.");

            var downloadUrl = check.DownloadUrl!;
            var preferZip = LooksLikeZipUrl(downloadUrl)
                || (check.AssetName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
            var downloadPath = Path.Combine(tempDir, preferZip ? "SoftPrint-update.zip" : "SoftPrint-Setup.exe");

            Set(new UpdateApplyStatus("downloading", 8, $"Baixando SoftPrint {check.LatestVersion}…", true, false, false));
            await DownloadAsync(downloadUrl, downloadPath).ConfigureAwait(false);

            var isZip = preferZip || IsZipFile(downloadPath);
            Set(new UpdateApplyStatus("installing", 92, "Instalando atualização…", true, false, false));

            if (isZip)
            {
                var extractDir = Path.Combine(tempDir, "extracted");
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, recursive: true);
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
                var payloadDir = ResolvePayloadDirectory(extractDir);
                WriteZipRestartScript(scriptPath, payloadDir);
            }
            else
            {
                WriteExeRestartScript(scriptPath, downloadPath);
            }

            LaunchDetached(scriptPath);

            Set(new UpdateApplyStatus("restarting", 100, "Reiniciando o SoftPrint…", true, false, true));
            await Task.Delay(800).ConfigureAwait(false);
            _lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao aplicar atualização");
            Set(new UpdateApplyStatus("failed", Status.Percent, "Falha na atualização.", false, true, false, ex.Message));
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task DownloadAsync(string url, string destination)
    {
        var opts = _options.CurrentValue;
        var client = _httpClientFactory.CreateClient("softprint-update-download");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"SoftPrint/{SoftPrintVersion.Current}");
        // Necessário para a URL api.github.com/.../releases/assets/{id}
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        var token = FirstNonEmpty(
            opts.UpdateGitHubToken,
            Environment.GetEnvironmentVariable("UPDATE_GITHUB_TOKEN"),
            Environment.GetEnvironmentVariable("SOFTPRINT_GITHUB_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Download falhou ({(int)response.StatusCode}): {Truncate(body)}");
        }

        // GitHub às vezes devolve JSON de metadados se o Accept estiver errado.
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub devolveu JSON em vez do instalador. Verifique o token e a URL do asset.");

        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        var lastPercent = 8;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            readTotal += read;
            var percent = total > 0
                ? 8 + (int)(readTotal * 80L / total) // 8..88
                : Math.Min(88, lastPercent + 1);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                var mb = readTotal / (1024d * 1024d);
                var msg = total > 0
                    ? $"Baixando… {mb:0.0} / {total / (1024d * 1024d):0.0} MB"
                    : $"Baixando… {mb:0.0} MB";
                Set(new UpdateApplyStatus("downloading", percent, msg, true, false, false));
            }
        }

        Set(new UpdateApplyStatus("downloading", 90, "Download concluído.", true, false, false));
    }

    private static bool LooksLikeZipUrl(string url)
    {
        if (url.Contains(".zip", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) < 2) return false;
            return header[0] == (byte)'P' && header[1] == (byte)'K';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deltas antigos às vezes vinham com pasta x64/x86; o instalador precisa da pasta onde está o .exe.
    /// </summary>
    internal static string ResolvePayloadDirectory(string extractDir)
    {
        if (string.IsNullOrWhiteSpace(extractDir) || !Directory.Exists(extractDir))
            return extractDir;

        static bool HasApp(string dir) =>
            File.Exists(Path.Combine(dir, "SoftPrint.exe")) ||
            File.Exists(Path.Combine(dir, "SoftPrint.Legacy.exe")) ||
            File.Exists(Path.Combine(dir, "softprint"));

        if (HasApp(extractDir))
            return extractDir;

        foreach (var name in new[] { "x64", "x86", "win-x64", "win-x86", "windows-modern-x64", "windows-modern-x86" })
        {
            var sub = Path.Combine(extractDir, name);
            if (HasApp(sub))
                return sub;
        }

        var dirs = Directory.GetDirectories(extractDir);
        if (dirs.Length == 1 && HasApp(dirs[0]))
            return dirs[0];

        return extractDir;
    }

    private static void WriteZipRestartScript(string scriptPath, string extractDir)
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "SoftPrint");
        var modern = Path.Combine(installDir, "SoftPrint.exe");
        var legacy = Path.Combine(installDir, "SoftPrint.Legacy.exe");
        var current = Environment.ProcessPath ?? "";
        var preferLegacy = current.Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        var fallback = File.Exists(current) ? current : modern;
        var pid = Environment.ProcessId;
        var hpatch = Path.Combine(extractDir, "hpatchz.exe");
        if (!File.Exists(hpatch))
            hpatch = Path.Combine(extractDir, "hpatchz");

        var lines = new List<string>
        {
            "@echo off",
            "setlocal",
            "timeout /t 2 /nobreak >nul",
            ":waitpid",
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto waitpid",
            ")",
            $"if not exist \"{installDir}\" mkdir \"{installDir}\"",
            $"set HPATCH={hpatch}",
            // Patches binários (delta): gera o exe novo a partir do instalado + .hdiff
            $"if exist \"%HPATCH%\" if exist \"{extractDir}\\SoftPrint.exe.hdiff\" if exist \"{modern}\" (",
            $"  \"%HPATCH%\" \"{modern}\" \"{extractDir}\\SoftPrint.exe.hdiff\" \"{extractDir}\\SoftPrint.exe\"",
            "  if errorlevel 1 echo SoftPrint hpatch SoftPrint.exe failed>> \"%TEMP%\\softprint-update-error.txt\"",
            ")",
            $"if exist \"%HPATCH%\" if exist \"{extractDir}\\SoftPrint.Legacy.exe.hdiff\" if exist \"{legacy}\" (",
            $"  \"%HPATCH%\" \"{legacy}\" \"{extractDir}\\SoftPrint.Legacy.exe.hdiff\" \"{extractDir}\\SoftPrint.Legacy.exe\"",
            "  if errorlevel 1 echo SoftPrint hpatch SoftPrint.Legacy.exe failed>> \"%TEMP%\\softprint-update-error.txt\"",
            ")",
            // Copia só o que veio no pacote (delta ou completo), ignorando ferramentas de patch.
            $"robocopy \"{extractDir}\" \"{installDir}\" /E /IS /IT /NFL /NDL /NJH /NJS /NC /NS /NP /XF *.hdiff hpatchz.exe hpatchz softprint-delta.json >nul",
            "set ERR=%ERRORLEVEL%",
            "if %ERR% GEQ 8 (",
            "  echo SoftPrint robocopy exit %ERR%>> \"%TEMP%\\softprint-update-error.txt\"",
            ") else (",
            "  set ERR=0",
            ")",
            "timeout /t 1 /nobreak >nul",
            preferLegacy
                ? $"if exist \"{legacy}\" start \"\" \"{legacy}\" & goto done"
                : $"if exist \"{modern}\" start \"\" \"{modern}\" & goto done",
            $"if exist \"{modern}\" start \"\" \"{modern}\" & goto done",
            $"if exist \"{legacy}\" start \"\" \"{legacy}\" & goto done",
            $"if exist \"{fallback}\" start \"\" \"{fallback}\" & goto done",
            "echo SoftPrint update could not restart > \"%TEMP%\\softprint-update-error.txt\"",
            ":done",
            "endlocal"
        };
        File.WriteAllLines(scriptPath, lines);
    }

    private static void WriteExeRestartScript(string scriptPath, string setupPath)
    {
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "SoftPrint");
        var modern = Path.Combine(installDir, "SoftPrint.exe");
        var legacy = Path.Combine(installDir, "SoftPrint.Legacy.exe");
        var current = Environment.ProcessPath ?? "";
        var preferLegacy = current.Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        var fallback = File.Exists(current) ? current : modern;

        // /VERYSILENT aceita termos; a política customizada também é ignorada no modo silent (Inno).
        var lines = new[]
        {
            "@echo off",
            "setlocal",
            "timeout /t 2 /nobreak >nul",
            $"\"{setupPath}\" /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS",
            "set ERR=%ERRORLEVEL%",
            "timeout /t 2 /nobreak >nul",
            preferLegacy
                ? $"if exist \"{legacy}\" start \"\" \"{legacy}\" & goto done"
                : $"if exist \"{modern}\" start \"\" \"{modern}\" & goto done",
            $"if exist \"{modern}\" start \"\" \"{modern}\" & goto done",
            $"if exist \"{legacy}\" start \"\" \"{legacy}\" & goto done",
            $"if exist \"{fallback}\" start \"\" \"{fallback}\" & goto done",
            "echo SoftPrint update could not restart > \"%TEMP%\\softprint-update-error.txt\"",
            ":done",
            "if not \"%ERR%\"==\"0\" echo SoftPrint setup exit %ERR%>> \"%TEMP%\\softprint-update-error.txt\"",
            "endlocal"
        };
        File.WriteAllLines(scriptPath, lines);
    }

    private static void LaunchDetached(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Path.GetTempPath()
        };
        Process.Start(psi);
    }

    private void Set(UpdateApplyStatus status)
    {
        lock (_gate) _status = status;
    }

    private static UpdateApplyStatus Idle() =>
        new("idle", 0, "", false, false, false);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200];
}
