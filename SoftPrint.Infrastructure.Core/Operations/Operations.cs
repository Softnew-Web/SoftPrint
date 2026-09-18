using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Operations;

public sealed class FileLogProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly bool _enabled;
    private readonly object _gate = new();

    public FileLogProvider(IAppPaths paths, IOptions<SoftPrintFeatureOptions> options)
    {
        _enabled = options.Value.LogToFile;
        _directory = Path.Combine(paths.DataRoot, "logs");
        if (_enabled) Directory.CreateDirectory(_directory);
    }

    public FileLogProvider(IHostEnvironment environment, IOptions<SoftPrintFeatureOptions> options)
        : this(new Infrastructure.Persistence.UserAppPaths(environment), options)
    {
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _directory, _enabled, _gate);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, string directory, bool enabled, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => enabled && logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            var path = Path.Combine(directory, $"softprint-{DateTime.Now:yyyyMMdd}.log");
            lock (gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
                Rotate(directory);
            }
        }

        private static void Rotate(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "softprint-*.log")
                         .OrderByDescending(f => f).Skip(14))
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
    }
}

public sealed class DataMaintenanceWorker(
    IJobRepository jobs,
    IAppPaths paths,
    IOptions<SoftPrintFeatureOptions> options,
    ILogger<DataMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var days = Math.Max(1, options.Value.RetentionDays);
                var removed = jobs.PurgeOlderThan(DateTimeOffset.UtcNow.AddDays(-days));
                if (removed > 0)
                    logger.LogInformation("Retenção: removidos {Count} pedidos com mais de {Days} dias.", removed, days);

                Backup(paths.DataRoot);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha na manutenção de dados.");
            }

            var minutes = Math.Clamp(options.Value.BackupIntervalMinutes, 5, 24 * 60);
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }

    private static void Backup(string data)
    {
        if (!Directory.Exists(data)) return;
        var backupRoot = Path.Combine(data, "backups");
        Directory.CreateDirectory(backupRoot);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(backupRoot, stamp);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(data, "*.json"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        var key = Path.Combine(data, "api-key.txt");
        if (File.Exists(key)) File.Copy(key, Path.Combine(target, "api-key.txt"), true);

        foreach (var old in Directory.EnumerateDirectories(backupRoot).OrderByDescending(d => d).Skip(10))
        {
            try { Directory.Delete(old, true); } catch { /* ignore */ }
        }
    }
}
