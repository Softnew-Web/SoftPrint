using System.Security.Cryptography;
using System.Text;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Infrastructure.Auth;
using SoftPrint.Infrastructure.Hosting;
using SoftPrint.Infrastructure.Integrations;
using SoftPrint.Infrastructure.Operations;
using SoftPrint.Infrastructure.Printing;
using SoftPrint.UI;

namespace SoftPrint.Composition;

public static class ApplicationComposer
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.AddSoftPrintConfiguration();
        SoftPrint.Infrastructure.Hosting.LoopbackBindingGuard.Enforce(builder);
        builder.Services.AddSoftPrintCore();
        builder.Services.AddSingleton<IPrinterCatalog, WindowsPrinterCatalog>();
        builder.Services.AddSingleton<IPrinterPageMetrics, WindowsPrinterPageMetrics>();
        builder.Services.AddSingleton<TrayAppNotifier>();
        builder.Services.AddSingleton<IAppNotifier>(sp => sp.GetRequiredService<TrayAppNotifier>());
        builder.Services.AddSingleton<IWindowsStartupService, WindowsStartupService>();
        builder.Services.AddSingleton<IFolderOperations, WindowsFolderOperations>();
        builder.Services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(
            Platform: "windows",
            PrintingBackend: "windows-spooler",
            HasDesktopShell: true,
            HasNativeFolderPicker: true,
            StartupRegistration: "registry",
            IsLegacy: false));
        builder.Services.AddSingleton<IPrintStrategy, WindowsPrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, ImagePrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, PdfPrintStrategy>();
        builder.Services.AddSingleton<IPrintStrategy, EscPosPrintStrategy>();
        builder.Services.AddSingleton<INetworkPrinterInstaller, WindowsNetworkPrinterInstaller>();
        builder.Logging.AddSoftPrintFileLogging(builder.Environment, builder.Configuration);

        if (builder.Configuration.GetValue("SoftPrint:StartWithWindows", false))
            new WindowsStartupService(builder.Environment).ApplyFromOptions(true);

        return builder;
    }

    public static WebApplication BuildApplication(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseMiddleware<ApiKeyMiddleware>();
        return app;
    }

    public static void StartDashboardIfNeeded(
        WebApplication app, bool headless, bool startInTray, EventWaitHandle? showSignal)
    {
        if (headless) return;

        var apiKey = app.Services.GetRequiredService<IApiKeyProvider>().ApiKey;
        var tray = app.Services.GetRequiredService<TrayAppNotifier>();
        var features = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SoftPrintFeatureOptions>>().Value;

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = app.Urls.First();
            var host = new DashboardUiHost(app, address, apiKey, features, tray, startInTray, showSignal);
            var thread = new Thread(host.Run);
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = false;
            thread.Start();
        });
    }

    internal static Task RunStartupUpdateCheck(
        SoftPrint.UI.SplashForm splash,
        IUpdateChecker checker,
        IUpdateApplier applier,
        bool autoApplyMandatory)
    {
        return Task.Run(async () =>
        {
            try
            {
                checker.InvalidateCache();
                splash.SetLiveProgress(
                    null,
                    "Buscando versões…",
                    "Consultando o servidor de atualizações…");
                var check = await checker.CheckAsync().ConfigureAwait(false);
                ApplyUpdateCheckToSplash(splash, applier, check, autoApplyMandatory);
            }
            catch (Exception ex)
            {
                splash.SetLiveProgress(
                    null,
                    "Não foi possível buscar versões",
                    ex.Message,
                    resumeAfterMs: 2_500);
            }
        });
    }

    private static void ApplyUpdateCheckToSplash(
        SoftPrint.UI.SplashForm splash,
        IUpdateApplier applier,
        UpdateCheckResult check,
        bool autoApplyMandatory)
    {
        if (check.Error is not null)
        {
            splash.SetLiveProgress(null, "Não foi possível buscar versões", check.Error, resumeAfterMs: 2_500);
            return;
        }

        if (!check.UpdateAvailable || string.IsNullOrWhiteSpace(check.DownloadUrl))
        {
            splash.SetLiveProgress(
                null,
                "Versão em dia",
                $"Você já está na {check.CurrentVersion}.",
                resumeAfterMs: 2_200);
            return;
        }

        if (!check.Mandatory || !autoApplyMandatory)
        {
            splash.SetLiveProgress(
                null,
                $"Nova versão {check.LatestVersion} disponível",
                check.Mandatory
                    ? "Atualização recomendada — use Atualizar no painel."
                    : "Use Atualizar no painel quando quiser instalar.",
                resumeAfterMs: 2_800);
            return;
        }

        splash.SetLiveProgress(
            5,
            $"Instalando SoftPrint {check.LatestVersion}",
            "Preparando download…",
            lockProgress: true);
        if (!applier.TryStart(out var err)
            && !string.IsNullOrWhiteSpace(err)
            && !err.Contains("andamento", StringComparison.OrdinalIgnoreCase))
        {
            splash.SetLiveProgress(null, "Atualização adiada", err, resumeAfterMs: 2_500);
        }
    }

    internal static void SyncSplashWithUpdate(SoftPrint.UI.SplashForm splash, IUpdateApplier? updateApplier)
    {
        var status = updateApplier?.Status;
        if (status is null || (!status.InProgress && !status.Restarting && !status.Failed))
            return;

        if (status.Failed)
        {
            splash.SetLiveProgress(
                status.Percent,
                "Falha na atualização",
                status.Error ?? status.Message,
                lockProgress: true);
            return;
        }

        var title = status.Phase switch
        {
            "checking" => "Preparando atualização…",
            "downloading" => "Baixando nova versão…",
            "verifying" => "Verificando integridade…",
            "installing" => "Instalando arquivos…",
            "restarting" => "Reiniciando SoftPrint…",
            "starting" => "Iniciando atualização…",
            _ => status.Restarting ? "Reiniciando SoftPrint…" : "Atualizando SoftPrint…"
        };
        splash.SetLiveProgress(status.Percent, title, status.Message, lockProgress: true);
    }
}

public sealed class SoftPrintInstance : IDisposable
{
    private readonly Mutex _current;
    private readonly Mutex _legacy;

    public EventWaitHandle ShowRequested { get; }

    internal SoftPrintInstance(Mutex current, Mutex legacy, EventWaitHandle showRequested)
    {
        _current = current;
        _legacy = legacy;
        ShowRequested = showRequested;
    }

    public void Dispose()
    {
        _current.Dispose();
        _legacy.Dispose();
        ShowRequested.Dispose();
    }
}

public static class SingleInstanceGuard
{
    public static SoftPrintInstance? TryAcquire()
    {
        var suffix = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..24];
        var current = new Mutex(true, "Local\\SoftPrint-" + suffix, out var currentAcquired);
        var legacy = new Mutex(true, "Local\\AutoPrint-" + suffix, out var legacyAcquired);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SoftPrint-Show-" + suffix);
        if (currentAcquired && legacyAcquired)
            return new SoftPrintInstance(current, legacy, show);

        // Named wait handle: não Dispose o kernel object aqui — só o handle local.
        // Dispose total no processo secundário podia interferir no listener da 1ª instância.
        try { show.Set(); }
        finally
        {
            current.Dispose();
            legacy.Dispose();
            // Mantém o evento nomeado vivo para a instância principal.
            show.Close();
        }
        return null;
    }
}
