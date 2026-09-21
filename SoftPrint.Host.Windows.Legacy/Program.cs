using SoftPrint.Api.Endpoints;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Host.Windows.Legacy;
using SoftPrint.Infrastructure.Auth;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Infrastructure.Printing;
using Microsoft.Extensions.Configuration;

if (args.Contains("--diagnose"))
{
    LegacyBackgroundHost.EnsureConsole();
    Console.WriteLine(RuntimeDiagnostics.Report(
        "Windows Legacy", "windows-spooler",
        ("Windows 7+", OperatingSystem.IsWindowsVersionAtLeast(6, 1)),
        ("External browser UI", true)));
    return;
}

if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
{
    LegacyBackgroundHost.EnsureConsole();
    Console.Error.WriteLine("SoftPrint Legacy requer Windows 7 SP1 ou mais recente.");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddSoftPrintConfiguration();
builder.Services.AddSoftPrintCore();
builder.Services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
builder.Services.AddSingleton<IPrinterPageMetrics, WindowsPrinterPageMetrics>();
builder.Services.AddSingleton<IFolderOperations, LegacyFolderOperations>();
builder.Services.AddSingleton<IWindowsStartupService, LegacyStartupService>();
builder.Services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
    "windows-7", "windows-spooler", false, false, "registry", true));
builder.Services.AddSingleton<IPrintStrategy, WindowsPrintStrategy>();
builder.Services.AddSingleton<IPrintStrategy, ImagePrintStrategy>();
builder.Services.AddSingleton<IPrintStrategy, PdfPrintStrategy>();
builder.Services.AddSingleton<IPrintStrategy, EscPosPrintStrategy>();
builder.Services.AddSingleton<IAppNotifier, LegacyNoOpNotifier>();
builder.Services.AddSingleton<INetworkPrinterInstaller, WindowsNetworkPrinterInstaller>();
builder.Logging.AddSoftPrintFileLogging(builder.Environment, builder.Configuration);
if (builder.Configuration.GetValue("SoftPrint:StartWithWindows", false))
    new LegacyStartupService().ApplyFromOptions(true);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapWebDashboard();
app.MapJobEndpoints();
app.MapSettingsEndpoints();

var headless = builder.Configuration.GetValue<bool>("headless") || args.Contains("--headless");
var startInTray = args.Contains("--tray") || builder.Configuration.GetValue("SoftPrint:StartInTray", false);
if (!headless)
    LegacyBackgroundHost.StartTray(app, startInTray);
app.Run();
