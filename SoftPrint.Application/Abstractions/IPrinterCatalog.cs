using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface IPrinterCatalog
{
    IReadOnlyList<string> ListInstalled();
    IReadOnlyList<PrinterDeviceInfo> ListDetailed();
    bool IsInstalled(string printerName);
}
