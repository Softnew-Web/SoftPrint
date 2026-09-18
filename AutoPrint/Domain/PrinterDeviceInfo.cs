namespace AutoPrint.Domain;

/// <summary>Impressora instalada no Windows (USB, cabo, rede ou virtual).</summary>
public sealed record PrinterDeviceInfo(
    string Name,
    string Port,
    string Connection,
    string Driver,
    bool IsDefault,
    bool IsOffline,
    bool IsNetwork,
    bool IsLocal,
    bool IsShared,
    string Status)
{
    public string DisplayLabel =>
        IsDefault
            ? $"{Name}  [{Connection}] * padrao"
            : $"{Name}  [{Connection}]";
}
