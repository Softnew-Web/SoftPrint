using System.Diagnostics;

namespace SoftPrint.Infrastructure.Hosting;

public static class BrowserDashboard
{
    public static void OpenWhenReady(WebApplication app, string[] args)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase)) return;
            var address = (app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5178").TrimEnd('/');
            var url = address + "/dashboard";
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                }
            }
            catch
            {
                /* painel permanece acessível pela URL */
            }
        });
    }
}
