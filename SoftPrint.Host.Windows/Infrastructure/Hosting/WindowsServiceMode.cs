using System.Diagnostics;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Hosting;

public sealed class WindowsServiceMode : IWindowsServiceMode
{
    public ServiceModeStatus GetStatus()
    {
        var installed = WindowsServiceControl.IsCurrentExecutableInstalledAsService();
        var running = installed && WindowsServiceControl.IsRunning();
        return new ServiceModeStatus(
            Supported: true,
            Installed: installed,
            Running: running,
            CurrentProcessIsService: WindowsServiceControl.IsCurrentProcessTheService());
    }

    public ServiceModeChange SetEnabled(bool enabled)
    {
        var status = GetStatus();
        if (enabled)
        {
            if (status.Installed && status.Running)
                return new(true, false, "A impressão já continua depois do logoff.");

            if (status.Installed)
            {
                if (WindowsServiceControl.TryEnsureRunning(out var error))
                    return new(true, false, "Serviço iniciado. A impressão continua depois do logoff.");
                return new(false, false, error);
            }

            return Launch(
                "--install-service",
                "O Windows vai pedir permissão de administrador. Confirme para a impressão continuar após o logoff. O painel fecha e abre de novo.");
        }

        if (!status.Installed)
            return new(true, false, "O SoftPrint não está como serviço.");

        return Launch(
            "--uninstall-service",
            "Confirme a permissão de administrador. A impressão volta a parar no logoff.");
    }

    private static ServiceModeChange Launch(string arguments, string message)
    {
        var exe = Environment.ProcessPath ?? "SoftPrint.exe";
        Action start = () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true
            };
            if (!WindowsServiceControl.IsAdministrator())
                psi.Verb = "runas";
            Process.Start(psi);
        };

        if (WindowsServiceControl.IsAdministrator())
        {
            return new ServiceModeChange(true, false, message, () =>
            {
                Thread.Sleep(700);
                start();
            });
        }

        try
        {
            start();
            return new(true, true, message);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, false, "Permissão cancelada. O SoftPrint continua parando no logoff.");
        }
    }
}
