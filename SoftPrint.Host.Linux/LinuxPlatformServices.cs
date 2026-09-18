using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using SoftPrint.Printing.Linux;

namespace SoftPrint.Host.Linux;

public sealed class LinuxFolderOperations : IFolderOperations
{
    public bool CanBrowse => false;
    public string? Browse(string startPath) => null;
    public string Open(string path)
    {
        System.Diagnostics.Process.Start("xdg-open", path);
        return path;
    }
}

public sealed class NoOpNotifier : IAppNotifier
{
    public void NotifyCompleted(PrintJob job) { }
    public void NotifyUncertain(PrintJob job) { }
}

public sealed class SystemdStartupService : IWindowsStartupService
{
    private readonly IExternalCommandRunner _runner;

    public SystemdStartupService(IExternalCommandRunner? runner = null)
    {
        _runner = runner ?? ProcessCommandRunner.Instance;
    }

    public bool IsEnabled =>
        _runner.Capture("systemctl", "--user", "is-enabled", "softprint.service")
            .Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase);

    public void ApplyFromOptions(bool enable)
    {
        var args = enable
            ? new[] { "--user", "enable", "--now", "softprint.service" }
            : new[] { "--user", "disable", "--now", "softprint.service" };
        _runner.Run("systemctl", args);
    }
}
