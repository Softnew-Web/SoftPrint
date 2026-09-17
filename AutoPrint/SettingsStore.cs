using System.Text.Json;

record PrinterOptions(string PrinterName, bool Simulation, bool Paused, long Revision, DateTimeOffset UpdatedAt);
record SettingsRequest(string? PrinterName, bool Simulation, bool Paused, long ExpectedRevision);
sealed class SettingsConflictException : Exception;

sealed class SettingsStore
{
    private readonly object gate = new();
    private readonly string path;
    private PrinterOptions current;
    public PrinterOptions Current { get { lock (gate) return current; } }

    public SettingsStore(IHostEnvironment environment, IConfiguration configuration)
    {
        var directory = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "settings.json");
        current = File.Exists(path)
            ? JsonSerializer.Deserialize<PrinterOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException("Configuração inválida.")
            : new(configuration["AutoPrint:PrinterName"] ?? "", configuration.GetValue("AutoPrint:Simulation", true), false, 1, DateTimeOffset.UtcNow);
    }

    public PrinterOptions Update(SettingsRequest request)
    {
        lock (gate)
        {
            if (request.ExpectedRevision != current.Revision) throw new SettingsConflictException();
            var updated = new PrinterOptions(request.PrinterName ?? "", request.Simulation, request.Paused,
                current.Revision + 1, DateTimeOffset.UtcNow);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            current = updated;
            return current;
        }
    }
}
