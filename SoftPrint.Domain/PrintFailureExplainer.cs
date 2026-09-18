namespace SoftPrint.Domain;

/// <summary>Traduz exceções técnicas em mensagem + causa + local do processamento.</summary>
public static class PrintFailureExplainer
{
    public static (string Summary, string Reason, string Where) Explain(Exception exception, string fallbackWhere)
    {
        var message = exception.GetBaseException().Message?.Trim() ?? exception.Message;
        var where = InferWhere(message, fallbackWhere);
        var reason = InferReason(message);
        return (message, reason, where);
    }

    private static string InferWhere(string message, string fallbackWhere)
    {
        if (ContainsAny(message, "impressora", "printer", "spooler", "PrintDocument", "driver"))
            return "WindowsPrintStrategy → spooler do Windows";
        if (ContainsAny(message, "papel", "margem", "área de impressão", "fonte", "texto ao papel"))
            return "WindowsPrintStrategy → montagem da página";
        if (ContainsAny(message, "estratégia", "strategy"))
            return "PrintStrategyResolver";
        if (ContainsAny(message, "fila", "persist", "jobs.json"))
            return "JsonJobRepository";
        return fallbackWhere;
    }

    private static string InferReason(string message)
    {
        if (ContainsAny(message, "não encontrada", "not found", "IsValid"))
            return "A impressora configurada não está instalada ou não responde no Windows. Atualize a lista e salve de novo.";
        if (ContainsAny(message, "Configure o nome", "nome exato"))
            return "Nenhuma impressora foi escolhida com a simulação desligada. Selecione uma impressora e salve.";
        if (ContainsAny(message, "Área de impressão", "margens", "papel"))
            return "O driver reportou área de impressão inválida. Confira tamanho do papel e margens nas preferências da impressora.";
        if (ContainsAny(message, "ajustar o texto", "área gráfica"))
            return "Não foi possível desenhar o texto na página. O conteúdo pode estar vazio ou o papel/driver está inconsistente.";
        if (ContainsAny(message, "interrompido"))
            return "O SoftPrint foi fechado ou reiniciado no meio do envio. O papel pode ou não ter saído — confira antes de reenviar.";
        if (ContainsAny(message, "access", "denied", "acesso", "unauthorized"))
            return "O Windows bloqueou o acesso ao spooler ou à impressora. Verifique permissões e se a impressora não está pausada.";
        return "Falha durante o envio. O status ficou como 'conferir' para evitar reimpressão automática duplicada.";
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}
