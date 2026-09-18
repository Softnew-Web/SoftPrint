using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Persistence;

public sealed class UserAppPaths : IAppPaths
{
    public string DataRoot { get; }
    public string ConfigRoot { get; }

    public UserAppPaths(IHostEnvironment environment)
    {
        ConfigRoot = ResolveConfigRoot();
        DataRoot = ResolveDataRoot();
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(DataRoot);
        Migrate(environment.ContentRootPath);
    }

    public static string ResolveConfigRoot()
    {
        if (Environment.GetEnvironmentVariable("SOFTPRINT_CONFIG_ROOT") is { Length: > 0 } configOverride)
            return Path.GetFullPath(configOverride);
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SoftPrint");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? Path.Combine(xdg, "softprint")
            : Path.Combine(home, ".config", "softprint");
    }

    public static string ResolveDataRoot()
    {
        if (Environment.GetEnvironmentVariable("SOFTPRINT_DATA_ROOT") is { Length: > 0 } overrideRoot)
            return Path.GetFullPath(overrideRoot);
        if (OperatingSystem.IsWindows())
            return Path.Combine(ResolveConfigRoot(), "data");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdgData
            ? Path.Combine(xdgData, "softprint")
            : Path.Combine(home, ".local", "share", "softprint");
    }

    private void Migrate(string contentRoot)
    {
        CopyTree(Path.Combine(contentRoot, "data"), DataRoot);
        CopyTree(Path.Combine(contentRoot, "logs"), Path.Combine(DataRoot, "logs"));

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var autoPrint = Path.Combine(local, "AutoPrint");
            CopyTree(Path.Combine(autoPrint, "data"), DataRoot);
            CopyKnownFiles(autoPrint, DataRoot);
            CopyFile(Path.Combine(autoPrint, "system-settings.json"), Path.Combine(ConfigRoot, "system-settings.json"));
            CopyTree(Path.Combine(autoPrint, "logs"), Path.Combine(DataRoot, "logs"));
            CopyTree(Path.Combine(autoPrint, "WebView2"), Path.Combine(local, "SoftPrint", "WebView2"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            CopyTree(Path.Combine(home, ".config", "autoprint"), ConfigRoot);
            CopyTree(Path.Combine(home, ".local", "share", "autoprint"), DataRoot);
        }
    }

    private static readonly string[] KnownFiles =
    [
        "jobs.json", "settings.json", "api-key.txt", "webhook-retry.json"
    ];

    private static void CopyKnownFiles(string sourceRoot, string destinationRoot)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var name in KnownFiles)
            CopyFile(Path.Combine(sourceRoot, name), Path.Combine(destinationRoot, name));
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("WebView2", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("backups", StringComparison.OrdinalIgnoreCase))
                continue;
            CopyFile(file, Path.Combine(destination, relative));
        }
    }

    private static void CopyFile(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
    }
}
