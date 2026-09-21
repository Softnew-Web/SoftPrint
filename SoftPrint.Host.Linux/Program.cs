using SoftPrint.Api.Endpoints;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Host.Linux;
using SoftPrint.Infrastructure.Auth;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Printing.Linux;
using Microsoft.Extensions.Configuration;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Este host do SoftPrint requer Linux.");
    return;
}

if (args.Contains("--diagnose"))
{
    Console.WriteLine(RuntimeDiagnostics.Report(
        "Linux", "cups",
        ("CUPS lp", ProcessCommandRunner.ExistsOnPath("lp")),
        ("CUPS lpstat", ProcessCommandRunner.ExistsOnPath("lpstat")),
        ("xdg-open", ProcessCommandRunner.ExistsOnPath("xdg-open"))));
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddSoftPrintConfiguration();
builder.Services.AddSoftPrintCore();
builder.Services.AddSingleton<IExternalCommandRunner, ProcessCommandRunner>();
builder.Services.AddSingleton<IPrinterCatalog, CupsPrinterCatalog>();
builder.Services.AddSingleton<IPrinterPageMetrics, LinuxPrinterPageMetrics>();
builder.Services.AddSingleton<IFolderOperations, LinuxFolderOperations>();
builder.Services.AddSingleton<IWindowsStartupService, SystemdStartupService>();
builder.Services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
    "linux", "cups", false, false, "systemd-user", false));
builder.Services.AddSingleton<IPrintStrategy, CupsPrintStrategy>();
builder.Services.AddSingleton<IPrintStrategy, TcpEscPosPrintStrategy>();
builder.Services.AddSingleton<IAppNotifier, NoOpNotifier>();
builder.Services.AddSingleton<INetworkPrinterInstaller, UnsupportedNetworkPrinterInstaller>();
builder.Logging.AddSoftPrintFileLogging(builder.Environment, builder.Configuration);
if (builder.Configuration.GetValue("SoftPrint:StartWithWindows", false))
    new SystemdStartupService().ApplyFromOptions(true);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapWebDashboard();
app.MapJobEndpoints();
app.MapSettingsEndpoints();
BrowserDashboard.OpenWhenReady(app, args);
app.Run();
