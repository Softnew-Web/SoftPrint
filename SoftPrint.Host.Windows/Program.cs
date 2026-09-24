using SoftPrint.Api.Endpoints;
using SoftPrint.Application.Abstractions;
using SoftPrint.Composition;
using SoftPrint.Application.Services;
using SoftPrint.Domain;

if (args.Contains("--diagnose"))
{
    Console.WriteLine(RuntimeDiagnostics.Report(
        "Windows Modern", "windows-spooler",
        ("Windows 10+", OperatingSystem.IsWindowsVersionAtLeast(10)),
        ("WebView2 integration", SoftPrint.UI.Dashboard.HasWebView2Runtime())));
    return;
}

if (!OperatingSystem.IsWindowsVersionAtLeast(10))
{
    MessageBox.Show(
        "Este executável requer Windows 10 ou mais recente. Use SoftPrint.Legacy.exe neste computador.",
        "SoftPrint",
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);
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
    MessageBox.Show(
        "O SoftPrint já está em execução.\n\nSe o painel não aparecer, clique duas vezes no ícone da bandeja (perto do relógio) ou use Abrir painel no menu.",
        "SoftPrint",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    return;
}

var builder = ApplicationComposer.CreateBuilder(args);
var app = ApplicationComposer.BuildApplication(builder);
lifecycle = app.Services.GetService<IAppLifecycleLogger>();

var headless = builder.Configuration.GetValue<bool>("headless") || args.Contains("--headless");
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
        MessageBox.Show(
            "Não foi possível iniciar o SoftPrint. Verifique se ele já está aberto.\n\n" + exception.Message,
            "SoftPrint", MessageBoxButtons.OK, MessageBoxIcon.Error);
    throw;
}
