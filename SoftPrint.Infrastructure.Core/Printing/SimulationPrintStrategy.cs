using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Infrastructure.Printing;

public sealed class SimulationPrintStrategy : IPrintStrategy
{
    public bool CanHandle(PrintJob job, PrintOptions settings) => settings.Simulation;

    public Task<JobStatus> ExecuteAsync(PrintJob job, PrintOptions settings, CancellationToken cancellationToken) =>
        Task.FromResult(JobStatus.Simulated);
}
