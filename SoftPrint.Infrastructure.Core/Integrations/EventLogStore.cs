using System.Diagnostics;
using System.Text.Json;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Integrations;

public sealed class EventLogStore(
    IAppPaths paths,
    ISystemSettingsRepository systemSettings,
    IOptions<SoftPrintFeatureOptions> options) : IEventLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();

    public string FolderPath
    {
        get
        {
            var configured = options.Value.EventLogFolder?.Trim();
            if (string.IsNullOrWhiteSpace(configured))
                configured = "logs";
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(paths.DataRoot, configured);
        }
    }

    public void Write(JobFinishedEventLog entry)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(FolderPath);
            var path = Path.Combine(FolderPath, $"events-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            TrimOld();
        }
    }

    public IReadOnlyList<JobFinishedEventLog> ReadDay(DateOnly day)
    {
        var path = Path.Combine(FolderPath, $"events-{day:yyyy-MM-dd}.log");
        if (!File.Exists(path)) return [];

        var list = new List<JobFinishedEventLog>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<JobFinishedEventLog>(line, JsonOptions);
                if (item is not null) list.Add(item);
            }
            catch { /* skip bad lines */ }
        }

        return list;
    }

    public IReadOnlyList<DateOnly> ListDays()
    {
        if (!Directory.Exists(FolderPath)) return [];
        return Directory.EnumerateFiles(FolderPath, "events-*.log")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(name => name.StartsWith("events-", StringComparison.OrdinalIgnoreCase)
                ? name["events-".Length..]
                : null)
            .Where(d => d is not null && DateOnly.TryParseExact(d, "yyyy-MM-dd", out _))
            .Select(d => DateOnly.ParseExact(d!, "yyyy-MM-dd"))
            .OrderByDescending(d => d)
            .ToArray();
    }

    public void OpenFolder()
    {
        Directory.CreateDirectory(FolderPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = FolderPath,
            UseShellExecute = true
        });
    }

    private void TrimOld()
    {
        // Mantém os N dias mais recentes; ao gerar o (N+1), apaga o mais antigo.
        var keepDays = Math.Max(1, systemSettings.Current.EventLogRetentionDays);
        foreach (var day in ListDays().Skip(keepDays))
        {
            try { File.Delete(Path.Combine(FolderPath, $"events-{day:yyyy-MM-dd}.log")); }
            catch { /* ignore */ }
        }
    }
}
