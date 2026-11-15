namespace SoftPrint.Application;

/// <summary>
/// Onde fica o estado local do SoftPrint que não é configuração nem dado de fila: histórico e
/// trabalho do update, versão anterior, avisos de erro, perfil do WebView2 e arquivos temporários.
/// <para>
/// Sem raiz explícita é %LocalAppData%\SoftPrint do processo (como sempre foi). Com
/// <c>--config-root</c> / <c>SOFTPRINT_CONFIG_ROOT</c> (ou só <c>SOFTPRINT_DATA_ROOT</c>), tudo
/// isso fica debaixo da raiz informada, e nada do perfil real do usuário é lido nem gravado.
/// </para>
/// <para>
/// Exceção: o serviço do Windows. Ele roda como LocalSystem com <c>--config-root</c> apontando
/// para a pasta do usuário só para achar a configuração dele; o trabalho do update continua no
/// perfil do SYSTEM, porque o SYSTEM executa os scripts .cmd de lá — numa pasta que o usuário
/// grava, ele trocaria o script e ganharia SYSTEM.
/// </para>
/// </summary>
public static class SoftPrintStateLocation
{
    public const string ConfigRootVariable = "SOFTPRINT_CONFIG_ROOT";
    public const string DataRootVariable = "SOFTPRINT_DATA_ROOT";
    private const string ConfigRootArgument = "--config-root";

    private static volatile bool _markedAsService;

    /// <summary>
    /// Lê <c>--config-root X</c> / <c>--config-root=X</c> e publica em SOFTPRINT_CONFIG_ROOT —
    /// antes de qualquer caminho ser calculado. Fica no ambiente do processo, então os scripts de
    /// atualização e o SoftPrint reaberto por eles herdam a mesma raiz.
    /// </summary>
    public static void ApplyConfigRootArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? value = null;
            if (arg.StartsWith(ConfigRootArgument + "=", StringComparison.OrdinalIgnoreCase))
                value = arg.Substring(ConfigRootArgument.Length + 1);
            else if (string.Equals(arg, ConfigRootArgument, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                value = args[++i];

            if (string.IsNullOrWhiteSpace(value))
                continue;

            Environment.SetEnvironmentVariable(ConfigRootVariable, value);
            return;
        }
    }

    /// <summary>O host do Windows chama ao subir como serviço (além do <c>--service</c> na linha de comando).</summary>
    public static void MarkAsWindowsService() => _markedAsService = true;

    public static bool IsWindowsService =>
        _markedAsService
        || Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));

    /// <summary>SOFTPRINT_CONFIG_ROOT (ou <c>--config-root</c>) já como caminho absoluto; null se não informado.</summary>
    public static string? ExplicitConfigRoot => FromEnvironment(ConfigRootVariable);

    /// <summary>SOFTPRINT_DATA_ROOT já como caminho absoluto; null se não informado.</summary>
    public static string? ExplicitDataRoot => FromEnvironment(DataRootVariable);

    /// <summary>
    /// Raiz informada explicitamente fora do serviço: nada fora dela pode ser lido ou gravado
    /// (sem migração do AutoPrint, sem %TEMP%, sem %LocalAppData%).
    /// </summary>
    public static bool IsIsolated => IsIsolatedFor(ExplicitConfigRoot, ExplicitDataRoot, IsWindowsService);

    /// <summary>Raiz do estado local (ver o resumo da classe).</summary>
    public static string LocalStateRoot =>
        ResolveLocalStateRoot(ExplicitConfigRoot, ExplicitDataRoot, IsWindowsService, DefaultLocalStateRoot);

    /// <summary>Perfil do WebView2 do painel.</summary>
    public static string WebView2Folder => Path.Combine(LocalStateRoot, "WebView2");

    /// <summary>Pasta para arquivos temporários: o %TEMP% do processo, ou uma subpasta da raiz isolada.</summary>
    public static string TempRoot => IsIsolated ? Path.Combine(LocalStateRoot, "temp") : Path.GetTempPath();

    /// <summary>%LocalAppData%\SoftPrint do processo — o lugar de sempre, sem raiz explícita.</summary>
    public static string DefaultLocalStateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoftPrint");

    internal static bool IsIsolatedFor(string? explicitConfigRoot, string? explicitDataRoot, bool windowsService) =>
        !windowsService && (explicitConfigRoot is not null || explicitDataRoot is not null);

    internal static string ResolveLocalStateRoot(
        string? explicitConfigRoot, string? explicitDataRoot, bool windowsService, string defaultRoot)
    {
        if (!IsIsolatedFor(explicitConfigRoot, explicitDataRoot, windowsService))
            return defaultRoot;
        return explicitConfigRoot ?? explicitDataRoot!;
    }

    private static string? FromEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : null;
}
