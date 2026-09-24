using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface IPrintJobFactory
{
    PrintJob Create(
        string reference,
        string text,
        string jobType = "default",
        JobContentKind contentKind = JobContentKind.Text,
        string? sourcePath = null,
        string? templateName = null,
        Guid? reprintedFromId = null);
}

public interface ITemplateRenderer
{
    string? Apply(string? templateName, string reference, string text, string jobType);
    IReadOnlyDictionary<string, string> List();
}

public interface IPrinterRouter
{
    string ResolvePrinter(string jobType, string fallbackPrinter);
}

public interface IWebhookNotifier
{
    Task NotifyFinishedAsync(PrintJob job, CancellationToken cancellationToken = default);
}

public interface IAppNotifier
{
    void NotifyUncertain(PrintJob job);
    void NotifyCompleted(PrintJob job);
    void NotifyUpdateAvailable(string currentVersion, string latestVersion, bool mandatory);
    void NotifyUpdateFailed(string message);
    void NotifyQueueAlert(string title, string message);
}

public interface IWindowsStartupService
{
    bool IsEnabled { get; }
    void ApplyFromOptions(bool enable);
}
