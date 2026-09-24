using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;

namespace SoftPrint.Infrastructure.Operations;

public sealed class SupportBundleService(
    IAppPaths paths,
    IEventLogStore events,
    ISystemSettingsRepository systemSettings,
    IPlatformCapabilities capabilities,
    JobQueueService jobs) : ISupportBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string CreateBundle()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var outDir = Path.Combine(paths.DataRoot, "support");
        Directory.CreateDirectory(outDir);
        var zipPath = Path.Combine(outDir, $"softprint-support-{stamp}.zip");

        var stage = Path.Combine(Path.GetTempPath(), "softprint-support-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            WriteManifest(stage);
            CopyLogs(stage);
            CopyIfExists(Path.Combine(paths.DataRoot, "session-state.json"), Path.Combine(stage, "session-state.json"));
            CopyDumps(stage);
            WriteRedactedSettings(stage);

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(stage, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* ignore */ }
        }
    }

    private void WriteManifest(string stage)
    {
        var pending = 0;
        var processing = 0;
        try
        {
            foreach (var j in jobs.List())
            {
                if (j.Status == JobStatus.Pending) pending++;
                else if (j.Status == JobStatus.Processing) processing++;
            }
        }
        catch { /* ignore */ }

        var manifest = new
        {
            createdAt = DateTimeOffset.Now,
            appVersion = SoftPrintVersion.Current,
            platform = capabilities.Platform,
            printingBackend = capabilities.PrintingBackend,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            dataRoot = paths.DataRoot,
            pending,
            processing,
            note = "Pacote de suporte SoftPrint — sem api-key, webhook secret nem .env."
        };
        File.WriteAllText(
            Path.Combine(stage, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            Encoding.UTF8);
    }

    private void CopyLogs(string stage)
    {
        var logsDir = Path.Combine(paths.DataRoot, "logs");
        var dest = Path.Combine(stage, "logs");
        Directory.CreateDirectory(dest);
        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "softprint-*.log")
                         .OrderByDescending(f => f).Take(30))
                CopyIfExists(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        try
        {
            foreach (var day in events.ListDays().Take(14))
            {
                var entries = events.ReadDay(day);
                if (entries.Count == 0) continue;
                var path = Path.Combine(dest, $"events-{day:yyyy-MM-dd}.log");
                var sb = new StringBuilder();
                foreach (var e in entries)
                    sb.AppendLine(JsonSerializer.Serialize(e, JsonOptions));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { /* ignore */ }
    }

    private void CopyDumps(string stage)
    {
        var dumps = Path.Combine(paths.DataRoot, "dumps");
        if (!Directory.Exists(dumps)) return;
        var dest = Path.Combine(stage, "dumps");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(dumps, "crash-*.txt")
                     .OrderByDescending(f => f).Take(10))
            CopyIfExists(file, Path.Combine(dest, Path.GetFileName(file)));
    }

    private void WriteRedactedSettings(string stage)
    {
        try
        {
            var s = systemSettings.Current;
            var redacted = new
            {
                s.SoundEnabled,
                s.LogToFile,
                s.RetentionDays,
                s.EventLogRetentionDays,
                s.BackupIntervalMinutes,
                s.TelemetryEnabled,
                telemetryUrl = string.IsNullOrWhiteSpace(s.TelemetryUrl) ? "" : "(definida)",
                webhookUrl = string.IsNullOrWhiteSpace(s.WebhookUrl) ? "" : "(definida)",
                webhookSecret = string.IsNullOrWhiteSpace(s.WebhookSecret) ? "" : "(redacted)",
                s.PrinterRoutes
            };
            File.WriteAllText(
                Path.Combine(stage, "system-settings.redacted.json"),
                JsonSerializer.Serialize(redacted, JsonOptions),
                Encoding.UTF8);
        }
        catch { /* ignore */ }
    }

    private static void CopyIfExists(string src, string dst)
    {
        if (!File.Exists(src)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
    }
}
