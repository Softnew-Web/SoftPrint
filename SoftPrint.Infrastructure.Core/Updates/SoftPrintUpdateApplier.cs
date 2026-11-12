using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Updates;

public sealed class SoftPrintUpdateApplier : IUpdateApplier
{
    private readonly IUpdateChecker _checker;
    private readonly IPreviousVersionStore _previous;
    private readonly IUpdateHistoryStore _history;
    private readonly IAppNotifier _notifier;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SoftPrintFeatureOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SoftPrintUpdateApplier> _logger;
    private readonly IRecentErrorLog _errorLog;
    private readonly object _gate = new();
    private UpdateApplyStatus _status = Idle();
    private int _running;

    public SoftPrintUpdateApplier(
        IUpdateChecker checker,
        IPreviousVersionStore previous,
        IUpdateHistoryStore history,
        IAppNotifier notifier,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SoftPrintFeatureOptions> options,
        IHostApplicationLifetime lifetime,
        IRecentErrorLog errorLog,
        ILogger<SoftPrintUpdateApplier> logger)
    {
        _checker = checker;
        _previous = previous;
        _history = history;
        _notifier = notifier;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _lifetime = lifetime;
        _errorLog = errorLog;
        _logger = logger;
    }

    public UpdateApplyStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public UpdateApplyStatus GetStatus() => Status;

    public bool TryStart(out string? error) =>
        TryStartCore(targetVersion: null, rollback: false, out error);

    public bool TryStartRollback(string? targetVersion, out string? error) =>
        TryStartCore(targetVersion, rollback: true, out error);

    private bool TryStartCore(string? targetVersion, bool rollback, out string? error)
    {
#if NET5_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
#else
        if (!SoftPrint.Infrastructure.Compat.OsHelper.IsWindows())
#endif
        {
            error = "Atualização automática disponível apenas no Windows. Use o pacote Linux.";
            return false;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            error = "Uma atualização já está em andamento.";
            return false;
        }

        var label = rollback ? "Preparando retorno de versão…" : "Preparando atualização…";
        Set(new UpdateApplyStatus("starting", 1, label, true, false, false));
        _ = Task.Run(() => RunAsync(targetVersion, rollback));
        error = null;
        return true;
    }

    private async Task RunAsync(string? targetVersion, bool rollback)
    {
        var applyVersion = SoftPrintVersion.Current;

        try
        {
            // Nunca no Temp compartilhado: no serviço (SYSTEM) seria C:\Windows\Temp, onde qualquer
            // usuário cria a pasta antes e troca o script/arquivos que o SYSTEM executa.
            var tempDir = PrepareWorkDirectory();
            var scriptPath = Path.Combine(tempDir, "apply-update.cmd");
            var installDir = SoftPrintInstallLocation.TryGetCurrent()
                ?? throw new InvalidOperationException(
                    "Não encontrei a pasta de instalação do SoftPrint. Instale a versão nova pelo SoftPrint-Setup.exe.");

            if (rollback)
            {
                Set(new UpdateApplyStatus("checking", 5, "Preparando versão anterior local…", true, false, false));
                var previous = _previous.TryGet()
                    ?? throw new InvalidOperationException(
                        "Não há versão anterior salva neste computador. O SoftPrint guarda só a última instalação antes de cada atualização.");

                if (!string.IsNullOrWhiteSpace(targetVersion) &&
                    !string.Equals(
                        SoftPrintVersionCompare.Normalize(targetVersion),
                        SoftPrintVersionCompare.Normalize(previous.Version),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Só a versão anterior local (v{previous.Version}) pode ser restaurada.");
                }

                applyVersion = previous.Version;
                _history.RecordApplyStarted(applyVersion, rollback: true);
                VerifyRollbackPayload(previous.DirectoryPath);
                Set(new UpdateApplyStatus(
                    "installing", 40,
                    $"Restaurando SoftPrint {previous.Version}…",
                    true, false, false));
                var restorePlan = LaunchPlan.For(installDir, tempDir);
                WriteLocalRestoreScript(scriptPath, previous.DirectoryPath, restorePlan);
                Launch(scriptPath, restorePlan);
                PendingUpdateFinalizer.MarkPending(applyVersion, rollback: true);
                // Sucesso só na próxima subida (PendingUpdateFinalizer).
                Set(new UpdateApplyStatus("restarting", 100, "Reiniciando o SoftPrint…", true, false, true));
                await Task.Delay(800).ConfigureAwait(false);
                _lifetime.StopApplication();
                return;
            }

            Set(new UpdateApplyStatus("checking", 5, "Consultando versão no GitHub…", true, false, false));
            _checker.InvalidateCache();
            var check = await _checker.CheckAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(check.Error) && !check.UpdateAvailable)
                throw new InvalidOperationException(check.Error);

            if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
                throw new InvalidOperationException("Nenhuma atualização disponível para instalar.");

            applyVersion = check.LatestVersion ?? SoftPrintVersion.Current;
            _history.RecordApplyStarted(applyVersion, rollback: false);

            var downloadUrl = check.DownloadUrl!;
            var preferZip = LooksLikeZipUrl(downloadUrl)
                || (check.AssetName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
            var downloadPath = Path.Combine(tempDir, preferZip ? "SoftPrint-update.zip" : "SoftPrint-Setup.exe");

            Set(new UpdateApplyStatus("downloading", 8, $"Baixando SoftPrint {check.LatestVersion}…", true, false, false));
            await DownloadAsync(downloadUrl, downloadPath).ConfigureAwait(false);

            Set(new UpdateApplyStatus("verifying", 90, "Verificando integridade (SHA256)…", true, false, false));
            PackageIntegrity.EnsureMatches(downloadPath, check.Sha256);

            var isZip = preferZip || IsZipFile(downloadPath);
            Set(new UpdateApplyStatus("installing", 92, "Instalando atualização…", true, false, false));

            LaunchPlan plan;
            if (isZip)
            {
                var extractDir = Path.Combine(tempDir, "extracted");
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, recursive: true);
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
                var payloadDir = ResolvePayloadDirectory(extractDir);
                VerifyExtractedExecutables(payloadDir);
                plan = LaunchPlan.For(installDir, tempDir);
                WriteZipRestartScript(scriptPath, payloadDir, plan);
            }
            else
            {
                PackageIntegrity.EnsureAuthenticode(downloadPath, _options.CurrentValue.RequireSignedUpdates);
                // O SoftPrint-Setup.exe pede o UAC sozinho quando a instalação é para todos os usuários.
                plan = LaunchPlan.Inline(installDir, tempDir);
                WriteExeRestartScript(scriptPath, downloadPath, plan);
            }

            Launch(scriptPath, plan);
            PendingUpdateFinalizer.MarkPending(applyVersion, rollback: false);
            // Sucesso só na próxima subida (PendingUpdateFinalizer).

            Set(new UpdateApplyStatus("restarting", 100, "Reiniciando o SoftPrint…", true, false, true));
            await Task.Delay(800).ConfigureAwait(false);
            _lifetime.StopApplication();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // Usuário recusou o pedido de administrador: nada foi alterado e o SoftPrint segue aberto.
            const string message = "Atualização cancelada: o Windows pediu permissão de administrador e ela não foi concedida.";
            _logger.LogWarning("Atualização cancelada no UAC.");
            try { _history.RecordApplyFailed(applyVersion, rollback, message); } catch { /* ignore */ }
            Set(new UpdateApplyStatus("failed", Status.Percent, "Atualização cancelada.", false, true, false, message));
            Interlocked.Exchange(ref _running, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, rollback ? "Falha ao retroceder versão" : "Falha ao aplicar atualização");
            UpdateFailureNotice.Record(ex.Message);
            _errorLog.Record(rollback ? "rollback" : "atualização", ex.Message);
            try { _history.RecordApplyFailed(applyVersion, rollback, ex.Message); } catch { /* ignore */ }
            try { _notifier.NotifyUpdateFailed(ex.Message); } catch { /* ignore */ }
            Set(new UpdateApplyStatus(
                "failed", Status.Percent,
                rollback ? "Falha ao retroceder." : "Falha na atualização.",
                false, true, false, ex.Message));
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

    private void VerifyExtractedExecutables(string payloadDir)
    {
        var require = _options.CurrentValue.RequireSignedUpdates;
        foreach (var name in new[] { "SoftPrint.exe", "SoftPrint.Legacy.exe" })
        {
            var path = Path.Combine(payloadDir, name);
            if (!File.Exists(path)) continue;
            if (path.EndsWith(".hdiff", StringComparison.OrdinalIgnoreCase)) continue;
            PackageIntegrity.EnsureAuthenticode(path, require);
        }
    }

    private void VerifyRollbackPayload(string previousDir)
    {
        if (string.IsNullOrWhiteSpace(previousDir) || !Directory.Exists(previousDir))
            throw new InvalidOperationException("Pasta da versão anterior ausente ou inválida.");

        var modern = Path.Combine(previousDir, "SoftPrint.exe");
        var legacy = Path.Combine(previousDir, "SoftPrint.Legacy.exe");
        var exe = File.Exists(modern) ? modern : File.Exists(legacy) ? legacy : null;
        if (exe is null)
            throw new InvalidOperationException("Versão anterior sem SoftPrint.exe — rollback recusado.");

        var info = new FileInfo(exe);
        if (info.Length < 1024)
            throw new InvalidOperationException("Executável da versão anterior parece corrompido (arquivo muito pequeno).");

        PackageIntegrity.EnsureAuthenticode(exe, _options.CurrentValue.RequireSignedUpdates);
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

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: UAC recusado

    /// <summary>
    /// Como o script roda. <see cref="Elevate"/>: a pasta de instalação não é gravável por este
    /// processo (ex.: Program Files sem administrador) — o script de cópia roda com UAC e um
    /// segundo script, sem elevação, reabre o SoftPrint quando ele terminar (abrir o app a partir
    /// do script elevado o deixaria rodando como administrador).
    /// </summary>
    internal sealed record LaunchPlan(string InstallDir, string WorkDir, bool Elevate)
    {
        public string DoneMarker => Path.Combine(WorkDir, "install-done.txt");
        public string RestartScript => Path.Combine(WorkDir, "restart-softprint.cmd");

        public static LaunchPlan For(string installDir, string workDir) =>
            new(installDir, workDir, !SoftPrintInstallLocation.CanWrite(installDir));

        public static LaunchPlan Inline(string installDir, string workDir) =>
            new(installDir, workDir, false);
    }

    private static string PrepareWorkDirectory()
    {
        var root = Path.Combine(SoftPrintInstallLocation.LocalStateRoot, "update-work");
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            /* sobra de uma tentativa anterior presa — a subpasta nova é única */
        }

        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ErrorLog => UpdateFailureNotice.ScriptErrorPath;

    private static List<string> ScriptHeader()
    {
#if NET5_0_OR_GREATER
        var pid = Environment.ProcessId;
#else
        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
#endif
        return
        [
            "@echo off",
            "setlocal",
            "timeout /t 2 /nobreak >nul",
            ":waitpid",
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto waitpid",
            ")",
            // Matar qualquer processo SoftPrint residual para liberar o exe antes da cópia.
            "sc stop SoftPrint >nul 2>&1",
            "timeout /t 2 /nobreak >nul",
            "taskkill /F /IM SoftPrint.exe >nul 2>nul",
            "taskkill /F /IM SoftPrint.Legacy.exe >nul 2>nul",
            "timeout /t 1 /nobreak >nul"
        ];
    }

    /// <summary>Guarda a instalação atual como "versão anterior" (só uma cópia).</summary>
    private static IEnumerable<string> BackupCurrentLines(string installDir)
    {
        var previousDir = LocalPreviousVersionStore.PreviousDirectory;
        yield return $"if exist \"{previousDir}\" rmdir /s /q \"{previousDir}\"";
        yield return $"mkdir \"{previousDir}\"";
        yield return $"if exist \"{installDir}\" robocopy \"{installDir}\" \"{previousDir}\" /E /NFL /NDL /NJH /NJS /NC /NS /NP >nul";
        yield return $"echo {SoftPrintVersion.Current}> \"{LocalPreviousVersionStore.VersionMarkerPath}\"";
    }

    private static IEnumerable<string> StartAppLines(string installDir, string failureNote)
    {
        var modern = Path.Combine(installDir, "SoftPrint.exe");
        var legacy = Path.Combine(installDir, "SoftPrint.Legacy.exe");
#if NET6_0_OR_GREATER
        var current = Environment.ProcessPath ?? "";
#else
        var current = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
#endif
        var preferLegacy = current.Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        var trayFlag = Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)) ? " --tray" : "";

        yield return preferLegacy
            ? $"if exist \"{legacy}\" start \"\" \"{legacy}\"{trayFlag} & goto done"
            : $"if exist \"{modern}\" start \"\" \"{modern}\"{trayFlag} & goto done";
        yield return $"if exist \"{modern}\" start \"\" \"{modern}\"{trayFlag} & goto done";
        yield return $"if exist \"{legacy}\" start \"\" \"{legacy}\"{trayFlag} & goto done";
        yield return $"echo {failureNote}>> \"{ErrorLog}\"";
    }

    /// <summary>Fim do script: religa o serviço ou reabre o app (direto, ou pelo script sem elevação).</summary>
    private static IEnumerable<string> ScriptTrailer(LaunchPlan plan, string failureNote)
    {
        yield return "timeout /t 1 /nobreak >nul";
        if (plan.Elevate)
        {
            yield return "sc query SoftPrint >nul 2>&1";
            yield return "if not errorlevel 1 sc start SoftPrint >nul 2>&1";
            yield return $"echo ok> \"{plan.DoneMarker}\"";
            yield return "endlocal";
            yield break;
        }

        yield return "sc query SoftPrint >nul 2>&1";
        yield return "if errorlevel 1 goto startapp";
        yield return "sc start SoftPrint >nul 2>&1";
        yield return $"if errorlevel 1 echo SoftPrint service start failed>> \"{ErrorLog}\"";
        yield return "goto done";
        yield return ":startapp";
        foreach (var line in StartAppLines(plan.InstallDir, failureNote))
            yield return line;
        yield return ":done";
        yield return "endlocal";
    }

    private static void WriteRestartScript(LaunchPlan plan)
    {
        var lines = new List<string>
        {
            "@echo off",
            "setlocal",
            "set /a WAITED=0",
            ":waitdone",
            $"if exist \"{plan.DoneMarker}\" goto ready",
            // Script elevado não terminou em 15 min: reabre assim mesmo (versão que estiver lá).
            "if %WAITED% GEQ 900 goto ready",
            "timeout /t 1 /nobreak >nul",
            "set /a WAITED+=1",
            "goto waitdone",
            ":ready",
            "sc query SoftPrint >nul 2>&1",
            "if not errorlevel 1 goto done"
        };
        lines.AddRange(StartAppLines(plan.InstallDir, "SoftPrint update could not restart"));
        lines.Add(":done");
        lines.Add("endlocal");
        File.WriteAllLines(plan.RestartScript, lines);
    }

    internal static void WriteZipRestartScript(string scriptPath, string extractDir, LaunchPlan plan)
    {
        var installDir = plan.InstallDir;
        var modern = Path.Combine(installDir, "SoftPrint.exe");
        var legacy = Path.Combine(installDir, "SoftPrint.Legacy.exe");
        var hpatch = Path.Combine(extractDir, "hpatchz.exe");
        if (!File.Exists(hpatch))
            hpatch = Path.Combine(extractDir, "hpatchz");

        var lines = ScriptHeader();
        lines.Add($"if not exist \"{installDir}\" mkdir \"{installDir}\"");
        lines.AddRange(BackupCurrentLines(installDir));
        lines.AddRange(new[]
        {
            $"set HPATCH={hpatch}",
            $"if exist \"%HPATCH%\" if exist \"{extractDir}\\SoftPrint.exe.hdiff\" if exist \"{modern}\" (",
            $"  \"%HPATCH%\" \"{modern}\" \"{extractDir}\\SoftPrint.exe.hdiff\" \"{extractDir}\\SoftPrint.exe\"",
            $"  if errorlevel 1 echo SoftPrint hpatch SoftPrint.exe failed>> \"{ErrorLog}\"",
            ")",
            $"if exist \"%HPATCH%\" if exist \"{extractDir}\\SoftPrint.Legacy.exe.hdiff\" if exist \"{legacy}\" (",
            $"  \"%HPATCH%\" \"{legacy}\" \"{extractDir}\\SoftPrint.Legacy.exe.hdiff\" \"{extractDir}\\SoftPrint.Legacy.exe\"",
            $"  if errorlevel 1 echo SoftPrint hpatch SoftPrint.Legacy.exe failed>> \"{ErrorLog}\"",
            ")",
            $"robocopy \"{extractDir}\" \"{installDir}\" /E /IS /IT /NFL /NDL /NJH /NJS /NC /NS /NP /XF *.hdiff hpatchz.exe hpatchz softprint-delta.json >nul",
            "set ERR=%ERRORLEVEL%",
            "if %ERR% GEQ 8 (",
            $"  echo SoftPrint robocopy exit %ERR%>> \"{ErrorLog}\"",
            ") else (",
            "  set ERR=0",
            ")"
        });
        lines.AddRange(ScriptTrailer(plan, "SoftPrint update could not restart"));
        File.WriteAllLines(scriptPath, lines);
    }

    internal static void WriteLocalRestoreScript(string scriptPath, string previousDir, LaunchPlan plan)
    {
        var installDir = plan.InstallDir;
        var lines = ScriptHeader();
        lines.AddRange(new[]
        {
            $"if not exist \"{previousDir}\" (",
            $"  echo SoftPrint previous version missing>> \"{ErrorLog}\"",
            "  goto restoredone",
            ")",
            $"if not exist \"{installDir}\" mkdir \"{installDir}\"",
            $"robocopy \"{previousDir}\" \"{installDir}\" /E /IS /IT /NFL /NDL /NJH /NJS /NC /NS /NP /XF version.txt >nul",
            "set ERR=%ERRORLEVEL%",
            "if %ERR% GEQ 8 (",
            $"  echo SoftPrint restore robocopy exit %ERR%>> \"{ErrorLog}\"",
            ") else (",
            "  set ERR=0",
            ")",
            ":restoredone"
        });
        lines.AddRange(ScriptTrailer(plan, "SoftPrint rollback could not restart"));
        File.WriteAllLines(scriptPath, lines);
    }

    internal static void WriteExeRestartScript(string scriptPath, string setupPath, LaunchPlan plan)
    {
        var lines = ScriptHeader();
        lines.AddRange(BackupCurrentLines(plan.InstallDir));
        lines.Add($"\"{setupPath}\" /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS");
        lines.Add($"if errorlevel 1 echo SoftPrint setup exit %ERRORLEVEL%>> \"{ErrorLog}\"");
        lines.AddRange(ScriptTrailer(plan, "SoftPrint update could not restart"));
        File.WriteAllLines(scriptPath, lines);
    }

    private static void Launch(string scriptPath, LaunchPlan plan)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = plan.WorkDir
        };
        if (!plan.Elevate)
        {
            Process.Start(psi);
            return;
        }

        WriteRestartScript(plan);
        psi.Verb = "runas"; // UAC agora, com o SoftPrint ainda aberto: recusar não fecha nada.
        Process.Start(psi);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{plan.RestartScript}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = plan.WorkDir
        });
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
