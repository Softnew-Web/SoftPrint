using SoftPrint.Api.Endpoints;
using SoftPrint.Composition;
using SoftPrint.Application.Services;

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

using var instance = SingleInstanceGuard.TryAcquire();
if (instance is null)
{
    if (!args.Contains("--headless"))
        MessageBox.Show("O SoftPrint desta pasta já está em segundo plano. Use o ícone na bandeja do sistema para abrir o painel.", "SoftPrint");
    return;
}

var builder = ApplicationComposer.CreateBuilder(args);
var app = ApplicationComposer.BuildApplication(builder);
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
    if (!headless)
        MessageBox.Show(
            "Não foi possível iniciar o SoftPrint. Verifique se ele já está aberto.\n\n" + exception.Message,
            "SoftPrint", MessageBoxButtons.OK, MessageBoxIcon.Error);
    throw;
}
