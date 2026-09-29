using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Infrastructure.Configuration;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Infrastructure.Persistence;
using SoftPrint.Infrastructure.Printing;

namespace SoftPrint.Host.Windows.Legacy
{
    internal static class LegacyOsHelper
    {
        public static bool IsWindowsVersionAtLeast(int major, int minor = 0, int build = 0, int revision = 0) =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && Environment.OSVersion.Version >= new Version(major, minor, build, revision);
    }

    internal static class Program
    {
        public static async Task Main(string[] args)
        {
            if (Array.IndexOf(args, "--diagnose") >= 0)
            {
                LegacyBackgroundHost.EnsureConsole();
                Console.WriteLine(RuntimeDiagnostics.Report(
                    "Windows Legacy", "windows-spooler",
                    ("Windows 7+", LegacyOsHelper.IsWindowsVersionAtLeast(6, 1)),
                    ("External browser UI", true)));
                return;
            }

            if (!LegacyOsHelper.IsWindowsVersionAtLeast(6, 1))
            {
                LegacyBackgroundHost.EnsureConsole();
                Console.Error.WriteLine("SoftPrint Legacy requer Windows 7 SP1 ou mais recente.");
                return;
            }

            var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((ctx, config) =>
                {
                    config.SetBasePath(AppContext.BaseDirectory);
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
                    config.AddSoftPrintEnvFile(AppContext.BaseDirectory);
                    config.AddLegacyAutoPrintAliases();

                    // Carrega system-settings.json do perfil do usuário (mesma lógica do UserAppPaths)
                    var userConfigRoot = UserAppPaths.ResolveConfigRoot();
                    System.IO.Directory.CreateDirectory(userConfigRoot);
                    config.AddJsonFile(
                        System.IO.Path.Combine(userConfigRoot, "system-settings.json"),
                        optional: true,
                        reloadOnChange: true);
                    config.AddEnvironmentVariables("SOFTPRINT_");
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<LegacyStartup>();
                    webBuilder.UseKestrel();
                    webBuilder.ConfigureKestrel(opts =>
                        opts.ListenLocalhost(GetPort(args)));
                })
                .ConfigureServices((ctx, services) =>
                {
                    // Registra opções e catálogo de status via IServiceCollection (versão netcoreapp3.1)
                    services.AddSoftPrintConfiguration(ctx.Configuration, AppContext.BaseDirectory);
                    services.AddSoftPrintCore();
                    services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
                    services.AddSingleton<IPrinterPageMetrics, WindowsPrinterPageMetrics>();
                    services.AddSingleton<IFolderOperations, LegacyFolderOperations>();
                    services.AddSingleton<IWindowsStartupService, LegacyStartupService>();
                    services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
                        "windows-7", "windows-spooler", false, false, "registry", true));
                    services.AddSingleton<IPrintStrategy, WindowsPrintStrategy>();
                    services.AddSingleton<IPrintStrategy, ImagePrintStrategy>();
                    services.AddSingleton<IPrintStrategy, PdfPrintStrategy>();
                    services.AddSingleton<IPrintStrategy, EscPosPrintStrategy>();
                    services.AddSingleton<IAppNotifier, LegacyNoOpNotifier>();
                    services.AddSingleton<INetworkPrinterInstaller, WindowsNetworkPrinterInstaller>();
                })
                .ConfigureLogging((ctx, logging) =>
                {
                    logging.AddSoftPrintFileLogging(ctx.HostingEnvironment, ctx.Configuration);
                })
                .Build();

            if (host.Services.GetRequiredService<IConfiguration>().GetValue("SoftPrint:StartWithWindows", false))
                new LegacyStartupService().ApplyFromOptions(true);

            var headless = Environment.GetEnvironmentVariable("SOFTPRINT_HEADLESS") == "1"
                           || Array.IndexOf(args, "--headless") >= 0;
            var startInTray = Array.IndexOf(args, "--tray") >= 0
                              || host.Services.GetRequiredService<IConfiguration>().GetValue("SoftPrint:StartInTray", false);

            if (!headless)
            {
                var address = $"http://127.0.0.1:{GetPort(args)}";
                var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                LegacyBackgroundHost.StartTray(lifetime, host.Services, address, startInTray);
            }

            await host.RunAsync().ConfigureAwait(false);
        }

        private static int GetPort(string[] args)
        {
            var url = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "";
            if (url.Contains(':'))
            {
                var last = url.Split(':');
                if (int.TryParse(last[last.Length - 1].TrimEnd('/'), out var p))
                    return p;
            }
            return 5178;
        }
    }
}
