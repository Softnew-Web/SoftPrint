namespace SoftPrint.Application.Abstractions;

public interface IPlatformCapabilities
{
    string Platform { get; }
    string PrintingBackend { get; }
    bool HasDesktopShell { get; }
    bool HasNativeFolderPicker { get; }
    string StartupRegistration { get; }
    bool IsLegacy { get; }
}

public interface IWindowsServiceMode
{
    ServiceModeStatus GetStatus();
    ServiceModeChange SetEnabled(bool enabled);
}

public sealed record ServiceModeStatus(
    bool Supported,
    bool Installed,
    bool Running,
    bool CurrentProcessIsService);

public sealed record ServiceModeChange(
    bool Ok,
    bool NeedsConfirmation,
    string Message,
    Action? AfterResponse = null);

public sealed record PlatformCapabilities(
    string Platform,
    string PrintingBackend,
    bool HasDesktopShell,
    bool HasNativeFolderPicker,
    string StartupRegistration,
    bool IsLegacy) : IPlatformCapabilities;
