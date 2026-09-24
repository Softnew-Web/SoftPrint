using System.Text.Json;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Infrastructure.Integrations;

/// <summary>
/// Sessão + logs de ciclo de vida (início, fechamento, desligamento do Windows, crash).
/// Usa o mesmo arquivo events-*.log do painel, com contentKind = lifecycle.
/// </summary>
public sealed class AppLifecycleLogger(IAppPaths paths, IEventLogStore events) : IAppLifecycleLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private string? _exitReason;
    private string? _exitDetail;
    private bool _started;
    private bool _stopped;

    private string SessionPath => Path.Combine(paths.DataRoot, "session-state.json");

    public void OnApplicationStarted()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;

            var previous = TryReadSession();
            if (previous is { Status: "running" })
            {
                WriteLifecycle(
                    AppExitReasons.UncleanExit,
                    "SoftPrint parou de forma inesperada na sessão anterior.",
                    $"Último início: {previous.StartedAt:G} · PID {previous.Pid} · versão {previous.Version}. " +
                    "Possíveis causas: crash, Gerenciador de tarefas, falta de energia ou reinício forçado.");
            }

            WriteSession(new SessionState("running", DateTimeOffset.Now, Environment.ProcessId, SoftPrintVersion.Current, null, null));
            WriteLifecycle(
                "started",
                "SoftPrint iniciado.",
                $"PID {Environment.ProcessId} · v{SoftPrintVersion.Current}");
        }
    }

    public void NoteExitReason(string reason, string? detail = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(reason)) return;
            // Motivos mais específicos ganham (shutdown/crash > host-stop).
            if (_exitReason is AppExitReasons.WindowsShutdown or AppExitReasons.WindowsLogoff or AppExitReasons.Crashed)
                return;
            _exitReason = reason.Trim();
            _exitDetail = detail?.Trim();
        }
    }

    public void OnApplicationStopping()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;

            var reason = _exitReason ?? AppExitReasons.HostStop;
            var (title, detail) = DescribeStop(reason, _exitDetail);
            WriteLifecycle(reason, title, detail);
            WriteSession(new SessionState(
                "stopped",
                DateTimeOffset.Now,
                Environment.ProcessId,
                SoftPrintVersion.Current,
                reason,
                DateTimeOffset.Now));
        }
    }

    public void OnCrash(string message, string? detail = null)
    {
        lock (_gate)
        {
            WriteCrash(message, detail);
        }
    }

    public void OnCrash(Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            var (title, detail) = FormatException(exception, context);
            WriteCrash(title, detail);
        }
    }

    private void WriteCrash(string message, string? detail)
    {
        _exitReason = AppExitReasons.Crashed;
        _exitDetail = detail ?? message;
        WriteLifecycle(
            AppExitReasons.Crashed,
            message,
            detail);
        try
        {
            WriteSession(new SessionState(
                "crashed",
                DateTimeOffset.Now,
                Environment.ProcessId,
                SoftPrintVersion.Current,
                AppExitReasons.Crashed,
                DateTimeOffset.Now));
        }
        catch
        {
            /* ignore */
        }
    }

    private static (string Title, string Detail) FormatException(Exception exception, string? context)
    {
        var title = string.IsNullOrWhiteSpace(context)
            ? $"SoftPrint crashou: {exception.GetType().Name}."
            : $"{context.Trim()} ({exception.GetType().Name}).";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(exception.Message);
        AppendExceptionChain(sb, exception, depth: 0);

        // ToString completo no final (stack + dados).
        sb.AppendLine();
        sb.AppendLine("--- stack ---");
        sb.Append(exception.ToString());

        var detail = sb.ToString().Trim();
        if (detail.Length > 8000)
            detail = detail[..8000] + "\n… (cortado)";

        return (title, detail);
    }

    private static void AppendExceptionChain(System.Text.StringBuilder sb, Exception exception, int depth)
    {
        if (depth > 8) return;

        if (exception is AggregateException agg)
        {
            var i = 0;
            foreach (var inner in agg.Flatten().InnerExceptions)
            {
                sb.AppendLine($"[{i}] {inner.GetType().FullName}: {inner.Message}");
                i++;
                if (i >= 10)
                {
                    sb.AppendLine("… mais exceções internas omitidas");
                    break;
                }
            }
            return;
        }

        var current = exception.InnerException;
        var level = 1;
        while (current is not null && level <= 8)
        {
            sb.AppendLine($"inner[{level}] {current.GetType().FullName}: {current.Message}");
            current = current.InnerException;
            level++;
        }
    }

    private void WriteLifecycle(string kind, string message, string? detail)
    {
        try
        {
            events.Write(new JobFinishedEventLog(
                EventType: "app.lifecycle",
                At: DateTimeOffset.Now,
                Id: Guid.NewGuid(),
                Reference: "SoftPrint",
                JobType: kind,
                ContentKind: "lifecycle",
                Status: kind,
                PrinterName: null,
                Error: message,
                ErrorReason: detail,
                ErrorWhere: "SoftPrint",
                FinishedAt: DateTimeOffset.Now,
                Steps: null,
                Delivery: "lifecycle",
                DeliveryDetail: SoftPrintVersion.Current));
        }
        catch
        {
            /* ignore — nunca derrubar o app por falha de log */
        }
    }

    private static (string Title, string Detail) DescribeStop(string reason, string? detail)
    {
        return reason switch
        {
            AppExitReasons.UserClosed => (
                "SoftPrint fechado pelo usuário.",
                detail ?? "Saída pelo menu da bandeja (Sair)."),
            AppExitReasons.WindowsShutdown => (
                "Windows está desligando.",
                detail ?? "O SoftPrint recebeu o sinal de desligamento do Windows."),
            AppExitReasons.WindowsLogoff => (
                "Usuário fez logoff no Windows.",
                detail ?? "Sessão Windows encerrada; SoftPrint também encerrou."),
            AppExitReasons.Crashed => (
                "SoftPrint encerrou após um crash.",
                detail ?? "Ver detalhes da exceção no log."),
            AppExitReasons.UpdateRestart => (
                "SoftPrint reiniciando após atualização.",
                detail ?? "Encerramento controlado para aplicar update."),
            AppExitReasons.UncleanExit => (
                "Saída inesperada detectada.",
                detail ?? ""),
            _ => (
                "SoftPrint encerrado.",
                detail ?? "Host ASP.NET parado."),
        };
    }

    private SessionState? TryReadSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void WriteSession(SessionState state)
    {
        try
        {
            Directory.CreateDirectory(paths.DataRoot);
            File.WriteAllText(SessionPath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            /* ignore */
        }
    }

    private sealed record SessionState(
        string Status,
        DateTimeOffset StartedAt,
        int Pid,
        string Version,
        string? StopReason,
        DateTimeOffset? StoppedAt);
}
