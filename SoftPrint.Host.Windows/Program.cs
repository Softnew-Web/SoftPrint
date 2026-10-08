using SoftPrint.Api.Endpoints;
using SoftPrint.Application.Abstractions;
using SoftPrint.Composition;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Diagnostics;

if (args.Contains("--diagnose"))
{
    Console.WriteLine(RuntimeDiagnostics.Report(
        "Windows Modern", "windows-spooler",
        ("Windows 10+", OperatingSystem.IsWindowsVersionAtLeast(10)),
        ("WebView2 integration", SoftPrint.UI.Dashboard.HasWebView2Runtime())));
    return;
}

if (Has(args, "--install-service"))
{
    Environment.ExitCode = WindowsServiceControl.Install(Has(args, "--quiet"));
    return;
}

if (Has(args, "--uninstall-service"))
{
    Environment.ExitCode = WindowsServiceControl.Uninstall(Has(args, "--quiet"));
    return;
}

WindowsServiceControl.ApplyConfigRootArgument(args);

var asService = WindowsServiceHelpers.IsWindowsService() || Has(args, "--service");
var headlessRequested = Has(args, "--headless")
    || string.Equals(Environment.GetEnvironmentVariable("HEADLESS"), "true", StringComparison.OrdinalIgnoreCase)
    || string.Equals(Environment.GetEnvironmentVariable("HEADLESS"), "1", StringComparison.OrdinalIgnoreCase);

if (!OperatingSystem.IsWindowsVersionAtLeast(10))
{
    if (!asService)
    {
        MessageBox.Show(
            "Este executável requer Windows 10 ou mais recente. Use SoftPrint.Legacy.exe neste computador.",
            "SoftPrint",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    Environment.ExitCode = 1;
    return;
}

// Serviço instalado para este exe: o processo da sessão só abre o painel.
// O motor fica na sessão 0 e sobrevive ao logoff.
if (!asService && WindowsServiceControl.IsCurrentExecutableInstalledAsService())
{
    if (!WindowsServiceControl.TryEnsureRunning(out var serviceError))
    {
        if (!headlessRequested)
        {
            MessageBox.Show(
                "O serviço do SoftPrint não está em execução.\n\n" + serviceError,
                "SoftPrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        Environment.ExitCode = 1;
        return;
    }

    if (headlessRequested)
        return;

    var startUiInTray = Has(args, "--tray") || Has(args, "--ui");
    Environment.ExitCode = ServiceDashboardClient.Run(startUiInTray);
    return;
}

// Preenchido após o Build; handlers de crash usam este ponte.
IAppLifecycleLogger? lifecycle = null;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    System.Diagnostics.Debug.WriteLine(e.ExceptionObject);
    try
    {
        if (e.ExceptionObject is Exception ex)
            lifecycle?.OnCrash(ex, e.IsTerminating
                ? "Exceção não tratada (processo encerrando)"
                : "Exceção não tratada");
        else
            lifecycle?.OnCrash(
                "SoftPrint crashou (exceção não tratada).",
                e.ExceptionObject?.ToString());
    }
    catch
    {
        /* ignore */
    }
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    System.Diagnostics.Debug.WriteLine(e.Exception);
    try { lifecycle?.OnCrash(e.Exception, "Exceção em tarefa (unobserved)"); }
    catch { /* ignore */ }
    e.SetObserved();
};

using var instance = SingleInstanceGuard.TryAcquire();
if (instance is null)
{
    // Já sinalizou a 1ª instância (ShowRequested). Avisa para o usuário não achar que “não abriu”.
    if (!asService)
    {
        MessageBox.Show(
            "O SoftPrint já está em execução em segundo plano.\n\n" +
            "O painel deve aparecer em instantes.\n\n" +
            "Se não aparecer: procure o ícone do SoftPrint perto do relógio (▲ na barra de tarefas) e clique duas vezes.",
            "SoftPrint",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    return;
}

// Chegamos aqui: somos a única instância legítima (mutex adquirido).
// Qualquer SoftPrint.exe ainda em execução é um zumbi (morreu antes de liberar a porta).
// Matamos antes de tentar bindar a porta para evitar "Address already in use".
// Sessão 0 é o serviço: não derrubar a fila que sobrevive ao logoff.
if (!asService)
    KillZombieSoftPrint();

var builder = ApplicationComposer.CreateBuilder(args);
var app = ApplicationComposer.BuildApplication(builder);
lifecycle = app.Services.GetService<IAppLifecycleLogger>();

var headless = asService || headlessRequested || builder.Configuration.GetValue<bool>("headless");
var startInTray = args.Contains("--tray") || builder.Configuration.GetValue("SoftPrint:StartInTray", false);

ApplicationComposer.StartDashboardIfNeeded(app, headless, startInTray, instance.ShowRequested);
app.MapWebDashboard();
app.MapJobEndpoints();
app.MapSettingsEndpoints();

try
{
    app.Run();
}
catch (Exception exception)
{
    try { lifecycle?.OnCrash(exception, "Falha ao iniciar o SoftPrint"); } catch { /* ignore */ }
    if (!headless)
    {
        var msg = IsPortConflict(exception)
            ? "A porta 5178 está em uso por outro programa.\n\n" +
              "Feche o programa que usa essa porta ou altere a porta em appsettings.json.\n\n" +
              exception.Message
            : "Não foi possível iniciar o SoftPrint.\n\n" + exception.Message;
        MessageBox.Show(msg, "SoftPrint", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    throw;
}

static void KillZombieSoftPrint()
{
    var currentPid = Environment.ProcessId;
    foreach (var name in new[] { "SoftPrint", "SoftPrint.Host.Windows" })
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == currentPid || p.SessionId == 0) continue;
                try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); }
                catch { /* ignore — processo pode já ter morrido */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* ignore */ }
    }
}

static bool Has(string[] args, string name) =>
    args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

static bool IsPortConflict(Exception ex)
{
    var msg = ex.Message + (ex.InnerException?.Message ?? "");
    return msg.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("10048", StringComparison.OrdinalIgnoreCase) // WSAEADDRINUSE
        || msg.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
}
