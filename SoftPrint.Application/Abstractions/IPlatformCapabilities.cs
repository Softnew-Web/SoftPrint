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

public sealed record PlatformCapabilities(
    string Platform,
    string PrintingBackend,
    bool HasDesktopShell,
    bool HasNativeFolderPicker,
    string StartupRegistration,
    bool IsLegacy) : IPlatformCapabilities;
