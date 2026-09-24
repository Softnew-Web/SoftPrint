using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Application.Services;

public sealed class PrintJobFactory : IPrintJobFactory
{
    public PrintJob Create(
        string reference,
        string text,
        string jobType = "default",
        JobContentKind contentKind = JobContentKind.Text,
        string? sourcePath = null,
        string? templateName = null,
        Guid? reprintedFromId = null) =>
        PrintJob.CreatePending(reference.Trim(), text, jobType, contentKind, sourcePath, templateName, reprintedFromId);
}

public sealed class TemplateRenderer(Microsoft.Extensions.Options.IOptions<SoftPrintFeatureOptions> options) : ITemplateRenderer
{
    public IReadOnlyDictionary<string, string> List() => options.Value.ParseTemplates();

    public string? Apply(string? templateName, string reference, string text, string jobType)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return null;
        var templates = options.Value.ParseTemplates();
        if (!templates.TryGetValue(templateName.Trim(), out var body))
            throw new ArgumentException($"Template '{templateName}' não encontrado no .env.");

        return body
            .Replace("{reference}", reference, StringComparison.OrdinalIgnoreCase)
            .Replace("{text}", text, StringComparison.OrdinalIgnoreCase)
            .Replace("{type}", jobType, StringComparison.OrdinalIgnoreCase)
            .Replace("{now}", DateTimeOffset.Now.ToString("dd/MM/yyyy HH:mm:ss"), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PrinterRouter(
    Microsoft.Extensions.Options.IOptions<SoftPrintFeatureOptions> options,
    ISystemSettingsRepository systemSettings) : IPrinterRouter
{
    public string ResolvePrinter(string jobType, string fallbackPrinter)
    {
        var fromSystem = ParseRoutes(systemSettings.Current.PrinterRoutes);
        if (fromSystem.TryGetValue(jobType, out var printer) && !string.IsNullOrWhiteSpace(printer))
            return printer;

        var routes = options.Value.ParseRoutes();
        if (routes.TryGetValue(jobType, out printer) && !string.IsNullOrWhiteSpace(printer))
            return printer;
        return fallbackPrinter;
    }

    private static IReadOnlyDictionary<string, string> ParseRoutes(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            map[part[..idx].Trim()] = part[(idx + 1)..].Trim();
        }
        return map;
    }
}

public sealed class JobQueueService(
    IJobRepository repository,
    IPrintJobFactory factory,
    ITemplateRenderer templates)
{
    public IReadOnlyList<PrintJob> List() => repository.Snapshot();

    public PrintJob? Get(Guid id) => repository.FindById(id);

    public PrintJob Submit(
        string reference,
        string text,
        string? jobType = null,
        string? contentKind = null,
        string? sourcePath = null,
        string? templateName = null)
    {
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Envie texto ou um arquivo (PDF/imagem).");
        if (!string.IsNullOrWhiteSpace(text) && text.Length > 50_000)
            throw new ArgumentException("Envie texto entre 1 e 50.000 caracteres.");
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 120)
            throw new ArgumentException("Informe uma referência única de até 120 caracteres.");

        var type = string.IsNullOrWhiteSpace(jobType) ? "default" : jobType.Trim();
        var kind = JobContentKindExtensions.FromWire(contentKind);
        if (kind is JobContentKind.Pdf or JobContentKind.Image)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new ArgumentException("Para PDF/imagem, informe sourcePath de um arquivo existente.");
        }

        var rendered = templates.Apply(templateName, reference.Trim(), text ?? "", type);
        var payload = rendered ?? text ?? "";
        if (kind == JobContentKind.Text && string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Texto final do pedido ficou vazio após o template.");

        return repository.Add(factory.Create(reference, payload, type, kind, sourcePath, templateName));
    }

    public PrintJob Reprint(Guid id)
    {
        var original = repository.FindById(id)
                       ?? throw new ArgumentException("Pedido original não encontrado.");
        if (original.Status is JobStatus.Pending or JobStatus.Processing)
            throw new ArgumentException("Aguarde o pedido atual terminar antes de reimprimir.");

        var reference = $"reprint-{original.Reference}-{DateTimeOffset.UtcNow:HHmmss}-{Random.Shared.Next(100, 999)}";
        if (reference.Length > 120) reference = reference[..120];

        return repository.Add(factory.Create(
            reference,
            original.Text,
            original.JobType,
            original.ContentKind,
            original.SourcePath,
            original.TemplateName,
            original.Id));
    }
}
