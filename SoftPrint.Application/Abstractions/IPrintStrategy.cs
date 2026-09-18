using SoftPrint.Domain;

namespace SoftPrint.Application.Abstractions;

public interface IPrintStrategy
{
    bool CanHandle(PrintJob job, PrintOptions settings);
    Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken);
}
