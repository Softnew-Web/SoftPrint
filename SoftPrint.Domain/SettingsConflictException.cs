namespace SoftPrint.Domain;

public sealed class SettingsConflictException : Exception
{
    public SettingsConflictException() : base("As configurações mudaram. Recarregue antes de salvar.")
    {
    }
}
