using System.Diagnostics;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using SoftPrint.Infrastructure.Persistence;

namespace SoftPrint.Hosting;

/// <summary>
/// Serviço do Windows (sessão 0). Continua imprimindo depois do logoff,
/// ao contrário do processo da bandeja, que o Windows encerra com a sessão.
/// </summary>
public static class WindowsServiceControl
{
    public const string ServiceName = "SoftPrint";
    private const string DisplayName = "SoftPrint";
    private const string Description =
        "Fila de impressão do SoftPrint. Continua ativa depois do logoff do Windows.";

    public static void ApplyConfigRootArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? value = null;
            if (arg.StartsWith("--config-root=", StringComparison.OrdinalIgnoreCase))
                value = arg["--config-root=".Length..];
            else if (string.Equals(arg, "--config-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                value = args[++i];

            if (string.IsNullOrWhiteSpace(value))
                continue;

            Environment.SetEnvironmentVariable("SOFTPRINT_CONFIG_ROOT", value);
            return;
        }
    }

    public static bool IsCurrentExecutableInstalledAsService()
    {
        var image = QueryImagePath();
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(exe))
            return false;
        return image.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCurrentProcessTheService() =>
        WindowsServiceHelpers.IsWindowsService()
        || Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));

    public static bool IsRunning()
    {
        var scmHandle = OpenSCManager(null, null, ScManagerConnect);
        if (scmHandle == IntPtr.Zero)
            return false;

        using var scm = new ServiceHandle(scmHandle);
        var serviceHandle = OpenService(scm, ServiceName, ServiceQueryStatus);
        if (serviceHandle == IntPtr.Zero)
            return false;

        using var service = new ServiceHandle(serviceHandle);
        return QueryServiceStatus(service, out var status)
               && status.CurrentState is ServiceRunning or ServiceStartPending;
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int Install(bool quiet)
    {
        if (!IsAdministrator())
        {
            if (quiet)
            {
                Report("Instalar o serviço exige administrador.", true, quiet: true);
                return 5;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? "SoftPrint.exe",
                    Arguments = "--install-service",
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Report(
                    "A instalação do serviço foi cancelada. É preciso confirmar o administrador para o SoftPrint continuar após o logoff.",
                    true,
                    quiet: false);
                return 5;
            }
        }

        try
        {
            var binPath = BuildBinPath();
            using var scm = OpenManager(ScManagerAllAccess);
            using var service = OpenOrCreate(scm, binPath);
            Configure(service);
            StopService(service);
            StopInteractiveCopies();
            if (!StartAndWait(service, out var error))
            {
                Report(
                    "O serviço foi registrado, mas não entrou em execução.\n\n" + error,
                    true,
                    quiet);
                return 1;
            }

            EnablePanelAtLogin();

            if (!quiet)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? "SoftPrint.exe",
                        Arguments = "--ui",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    /* o painel pode ser aberto pelo atalho */
                }
            }

            Report(
                "O SoftPrint agora é um serviço do Windows.\n\n" +
                "A fila continua imprimindo depois do logoff e quando o computador liga, mesmo sem ninguém logado. " +
                "No próximo login o painel abre de novo. Fechar o painel não para a impressão.\n\n" +
                "Use impressoras USB ou de IP instaladas neste computador. " +
                "Uma impressora ligada só na sua conta pode não aparecer para o serviço.\n\n" +
                "Para voltar ao modo anterior, clique de novo em Ativo após o logoff.",
                false,
                quiet);
            return 0;
        }
        catch (Exception ex)
        {
            Report("Não foi possível registrar o serviço do SoftPrint.\n\n" + ex.Message, true, quiet);
            return 1;
        }
    }

    public static int Uninstall(bool quiet)
    {
        if (!IsAdministrator())
        {
            if (quiet)
            {
                Report("Remover o serviço exige administrador.", true, quiet: true);
                return 5;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? "SoftPrint.exe",
                    Arguments = "--uninstall-service",
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Report("A remoção do serviço foi cancelada.", true, quiet: false);
                return 5;
            }
        }

        try
        {
            using var scm = OpenManager(ScManagerAllAccess);
            var handle = OpenService(scm, ServiceName, ServiceAllAccess);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    Report("O serviço do SoftPrint não está instalado.", false, quiet);
                    return 0;
                }

                throw new InvalidOperationException(Win32Message(error));
            }

            using var service = new ServiceHandle(handle);
            StopService(service);
            if (!DeleteService(service))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceMarkedForDelete)
                    throw new InvalidOperationException(Win32Message(error));
            }

            Report(
                "O serviço do SoftPrint foi removido.\n\n" +
                "A impressão volta a parar quando o usuário fizer logoff, a menos que o programa seja aberto de novo.",
                false,
                quiet);
            return 0;
        }
        catch (Exception ex)
        {
            Report("Não foi possível remover o serviço do SoftPrint.\n\n" + ex.Message, true, quiet);
            return 1;
        }
    }

    public static bool TryEnsureRunning(out string error)
    {
        error = "";
        var scmHandle = OpenSCManager(null, null, ScManagerConnect);
        if (scmHandle == IntPtr.Zero)
        {
            error = "Não foi possível consultar os serviços do Windows.";
            return false;
        }

        using var scm = new ServiceHandle(scmHandle);
        var serviceHandle = OpenService(scm, ServiceName, ServiceQueryStatus | ServiceStart);
        var canStart = serviceHandle != IntPtr.Zero;
        if (!canStart)
            serviceHandle = OpenService(scm, ServiceName, ServiceQueryStatus);
        if (serviceHandle == IntPtr.Zero)
        {
            error = "O serviço SoftPrint não está instalado.";
            return false;
        }

        using var service = new ServiceHandle(serviceHandle);
        if (!QueryServiceStatus(service, out var status))
        {
            error = "Não foi possível ler o status do serviço.";
            return false;
        }

        if (status.CurrentState is ServiceRunning)
            return true;
        if (status.CurrentState is ServiceStartPending)
            return WaitUntil(service, ServiceRunning, out error);

        if (!canStart)
        {
            error = "O serviço está parado e esta conta não pode iniciá-lo. Peça a um administrador para iniciar o SoftPrint em services.msc.";
            return false;
        }

        if (!StartService(service, 0, IntPtr.Zero))
        {
            var code = Marshal.GetLastWin32Error();
            if (code == ErrorServiceAlreadyRunning)
                return true;
            error = code == ErrorAccessDenied
                ? "Sem permissão para iniciar o serviço. Abra services.msc como administrador e inicie o SoftPrint."
                : Win32Message(code);
            return false;
        }

        return WaitUntil(service, ServiceRunning, out error);
    }

    public static string ResolveDashboardAddress()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Urls", out var urls))
                {
                    var first = urls.GetString()?.Split(';')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(first) &&
                        Uri.TryCreate(first, UriKind.Absolute, out var uri))
                        return NormalizeLoopback(uri);
                }
            }
        }
        catch
        {
            /* usa a porta padrão */
        }

        return "http://127.0.0.1:5178";
    }

    public static void WaitForListenPort()
    {
        var address = ResolveDashboardAddress();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return;

        var host = uri.Host is "0.0.0.0" or "+" or "*" ? "127.0.0.1" : uri.Host;
        var until = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < until)
        {
            try
            {
                using var tcp = new TcpClient();
                var wait = tcp.BeginConnect(host, uri.Port, null, null);
                if (wait.AsyncWaitHandle.WaitOne(400) && tcp.Connected)
                    return;
            }
            catch
            {
                /* ainda subindo */
            }

            Thread.Sleep(250);
        }
    }

    private static void EnablePanelAtLogin()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return;

        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, true)
                        ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKey, true);
        key?.SetValue("SoftPrint", $"\"{exe}\" --ui");
    }

    private static string BuildBinPath()
    {
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Não foi possível localizar o SoftPrint.exe.");
        var configRoot = UserAppPaths.ResolveConfigRoot();
        if (exe.Contains('"') || configRoot.Contains('"'))
            throw new InvalidOperationException("O caminho contém aspas e não pode ser registrado como serviço.");

        // O SCM recebe a linha de comando já com aspas em cada caminho.
        // Não envolva a string inteira em outro par de aspas.
        return $"\"{exe}\" --service --config-root \"{configRoot}\"";
    }

    private static ServiceHandle OpenOrCreate(ServiceHandle scm, string binPath)
    {
        var existing = OpenService(scm, ServiceName, ServiceAllAccess);
        if (existing != IntPtr.Zero)
        {
            var service = new ServiceHandle(existing);
            if (!ChangeServiceConfig(
                    service,
                    ServiceWin32OwnProcess,
                    ServiceAutoStart,
                    ServiceErrorNormal,
                    binPath,
                    null,
                    IntPtr.Zero,
                    "Spooler\0",
                    LocalSystemAccount,
                    null,
                    DisplayName))
                throw new InvalidOperationException(Win32Message(Marshal.GetLastWin32Error()));
            return service;
        }

        var created = CreateService(
            scm,
            ServiceName,
            DisplayName,
            ServiceAllAccess,
            ServiceWin32OwnProcess,
            ServiceAutoStart,
            ServiceErrorNormal,
            binPath,
            null,
            IntPtr.Zero,
            "Spooler\0",
            LocalSystemAccount,
            null);
        if (created == IntPtr.Zero)
            throw new InvalidOperationException(Win32Message(Marshal.GetLastWin32Error()));
        return new ServiceHandle(created);
    }

    private static void Configure(ServiceHandle service)
    {
        var description = new ServiceDescription { Text = Description };
        ChangeServiceConfig2(service, ServiceConfigDescription, ref description);

        var actions = new[]
        {
            new ScAction { Type = ScActionRestart, DelayMs = 5000 },
            new ScAction { Type = ScActionRestart, DelayMs = 5000 },
            new ScAction { Type = ScActionRestart, DelayMs = 5000 }
        };
        var size = Marshal.SizeOf<ScAction>();
        var buffer = Marshal.AllocHGlobal(size * actions.Length);
        try
        {
            for (var i = 0; i < actions.Length; i++)
                Marshal.StructureToPtr(actions[i], buffer + (i * size), false);

            var failure = new ServiceFailureActions
            {
                ResetPeriodSeconds = 24 * 60 * 60,
                ActionCount = actions.Length,
                Actions = buffer
            };
            ChangeServiceConfig2(service, ServiceConfigFailureActions, ref failure);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool StartAndWait(ServiceHandle service, out string error)
    {
        error = "";
        if (!StartService(service, 0, IntPtr.Zero))
        {
            var code = Marshal.GetLastWin32Error();
            if (code != ErrorServiceAlreadyRunning)
            {
                error = Win32Message(code);
                return false;
            }
        }

        return WaitUntil(service, ServiceRunning, out error);
    }

    private static void StopService(ServiceHandle service)
    {
        if (!QueryServiceStatus(service, out var status))
            return;
        if (status.CurrentState is ServiceStopped)
            return;

        ControlService(service, ServiceControlStop, ref status);
        WaitUntil(service, ServiceStopped, out _);
    }

    private static bool WaitUntil(ServiceHandle service, int desired, out string error)
    {
        error = "";
        var until = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < until)
        {
            if (!QueryServiceStatus(service, out var status))
            {
                error = Win32Message(Marshal.GetLastWin32Error());
                return false;
            }

            if (status.CurrentState == desired)
                return true;
            Thread.Sleep(300);
        }

        error = desired == ServiceRunning
            ? "O serviço não ficou ativo a tempo."
            : "O serviço não parou a tempo.";
        return false;
    }

    private static void StopInteractiveCopies()
    {
        var current = Environment.ProcessId;
        foreach (var name in new[] { "SoftPrint", "SoftPrint.Host.Windows" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == current || process.SessionId == 0)
                        continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                catch
                {
                    /* já encerrou, ou não há permissão */
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }

    private static string? QueryImagePath()
    {
        var scmHandle = OpenSCManager(null, null, ScManagerConnect);
        if (scmHandle == IntPtr.Zero)
            return null;

        using var scm = new ServiceHandle(scmHandle);
        var serviceHandle = OpenService(scm, ServiceName, ServiceQueryConfig);
        if (serviceHandle == IntPtr.Zero)
            return null;

        using var service = new ServiceHandle(serviceHandle);
        QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
            return null;

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!QueryServiceConfig(service, buffer, needed, out _))
                return null;

            var config = Marshal.PtrToStructure<ServiceConfigData>(buffer);
            return config.BinaryPathName == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(config.BinaryPathName);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceHandle OpenManager(uint access)
    {
        var handle = OpenSCManager(null, null, access);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(Win32Message(Marshal.GetLastWin32Error()));
        return new ServiceHandle(handle);
    }

    private static string NormalizeLoopback(Uri uri)
    {
        var host = uri.Host is "0.0.0.0" or "+" or "*" ? "127.0.0.1" : uri.Host;
        var builder = new UriBuilder(uri) { Host = host };
        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static void Report(string message, bool error, bool quiet)
    {
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "softprint-service.log");
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(error ? "ERRO" : "OK")} {message.Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch
        {
            /* sem pasta temporária */
        }

        if (quiet)
            return;

        try
        {
            MessageBox.Show(
                message,
                "SoftPrint",
                MessageBoxButtons.OK,
                error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
        }
        catch
        {
            /* sessão sem área de trabalho */
        }
    }

    private static string Win32Message(int code) => code switch
    {
        ErrorAccessDenied => "Acesso negado. Execute como administrador.",
        ErrorServiceDoesNotExist => "O serviço SoftPrint não existe.",
        ErrorServiceExists => "O serviço SoftPrint já existe.",
        ErrorServiceMarkedForDelete => "O serviço está marcado para exclusão. Reinicie o Windows e tente de novo.",
        ErrorServiceAlreadyRunning => "O serviço já está em execução.",
        _ => $"Erro do Windows {code}."
    };

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceErrorNormal = 0x00000001;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceStopped = 0x00000001;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceConfigDescription = 1;
    private const int ServiceConfigFailureActions = 2;
    private const int ScActionRestart = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const string LocalSystemAccount = "LocalSystem";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string serviceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateService(
        IntPtr hSCManager,
        string serviceName,
        string displayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2(IntPtr hService, int infoLevel, ref ServiceDescription info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2(IntPtr hService, int infoLevel, ref ServiceFailureActions info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartService(IntPtr hService, int numServiceArgs, IntPtr serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, int control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(IntPtr hService, IntPtr buffer, int bufferSize, out int bytesNeeded);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescription
    {
        public string? Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public int DelayMs;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceFailureActions
    {
        public int ResetPeriodSeconds;
        public string? RebootMessage;
        public string? Command;
        public int ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    private sealed class ServiceHandle : IDisposable
    {
        private readonly IntPtr _handle;
        public ServiceHandle(IntPtr handle) => _handle = handle;
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
                CloseServiceHandle(_handle);
        }

        public static implicit operator IntPtr(ServiceHandle handle) => handle._handle;
    }
}
