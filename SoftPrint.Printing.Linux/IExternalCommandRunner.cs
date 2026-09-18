namespace SoftPrint.Printing.Linux;

public interface IExternalCommandRunner
{
    string Capture(string fileName, params string[] args);
    int Run(string fileName, params string[] args);
    Task<int> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken);
}
