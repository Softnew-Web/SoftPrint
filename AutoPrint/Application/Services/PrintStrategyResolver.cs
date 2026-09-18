using AutoPrint.Application.Abstractions;
using AutoPrint.Domain;

namespace AutoPrint.Application.Services;

public sealed class PrintStrategyResolver(IEnumerable<IPrintStrategy> strategies)
{
    public IPrintStrategy Resolve(PrintJob job, PrintOptions settings) =>
        strategies.FirstOrDefault(s => s.CanHandle(job, settings))
        ?? throw new InvalidOperationException(
            $"Nenhuma estratégia para tipo de conteúdo '{job.ContentKind.ToWire()}' (simulação={settings.Simulation}).");
}
