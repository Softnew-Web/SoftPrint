using SoftPrint.Domain;

namespace SoftPrint.Application.Services;

/// <summary>Etiqueta de teste guiado: mesma para a tela, o Legacy e o comando remoto.</summary>
public static class GuidedTestPrint
{
    public static PrintJob Submit(JobQueueService jobs, SettingsService settings, string? origin = null)
    {
        var paper = settings.Current;
        var text =
            "TESTE GUIADO SOFTPRINT\n" +
            "----------------------\n" +
            $"Papel: {PrintSurfaceMapper.Describe(paper)}\n" +
            $"Impressora: {paper.PrinterName}\n" +
            $"Modo: {(paper.Simulation ? "simulação" : "real")}\n" +
            $"Quando: {DateTimeOffset.Now:HH:mm:ss}\n" +
            (string.IsNullOrWhiteSpace(origin) ? "" : $"Pedido por: {origin}\n") +
            "\n" +
            "Se o papel estiver em Padrão (200×70),\n" +
            "esta etiqueta deve caber na área imprimível.";
        return jobs.Submit(
            reference: "guia-" + DateTimeOffset.Now.ToString("HHmmss"),
            text: text,
            jobType: "default",
            templateName: null);
    }
}
