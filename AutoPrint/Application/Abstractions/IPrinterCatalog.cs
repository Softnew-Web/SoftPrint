using AutoPrint.Domain;

namespace AutoPrint.Application.Abstractions;

public interface IPrinterCatalog
{
    IReadOnlyList<string> ListInstalled();
    IReadOnlyList<PrinterDeviceInfo> ListDetailed();
    bool IsInstalled(string printerName);
}
