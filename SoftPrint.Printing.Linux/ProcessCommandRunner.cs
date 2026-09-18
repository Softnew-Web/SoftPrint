using System.Diagnostics;

namespace SoftPrint.Printing.Linux;

public sealed class ProcessCommandRunner : IExternalCommandRunner
{
    public static ProcessCommandRunner Instance { get; } = new();

    public string Capture(string fileName, params string[] args)
    {
        try
        {
            var start = Create(fileName, args);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var process = Process.Start(start);
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }

    public int Run(string fileName, params string[] args)
    {
        try
        {
            using var process = Process.Start(Create(fileName, args));
            if (process is null) return -1;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<int> RunAsync(
        string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var process = Process.Start(Create(fileName, args))
            ?? throw new InvalidOperationException($"Não foi possível executar {fileName}.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    public static bool ExistsOnPath(string fileName)
    {
        if (File.Exists(fileName)) return true;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Any(path =>
            File.Exists(Path.Combine(path, fileName)) ||
            File.Exists(Path.Combine(path, fileName + ".exe")));
    }

    private static ProcessStartInfo Create(string fileName, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
}
