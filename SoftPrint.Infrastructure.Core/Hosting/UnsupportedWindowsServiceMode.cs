using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Hosting;

public sealed class UnsupportedWindowsServiceMode : IWindowsServiceMode
{
    public ServiceModeStatus GetStatus() => new(false, false, false, false);

    public ServiceModeChange SetEnabled(bool enabled) =>
        new(false, false, "Este sistema não instala o serviço do Windows.");
}
