using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Application.Services;
using SoftPrint.Domain;
using SoftPrint.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Integrations;

/// <summary>
/// Liga o SoftPrint ao monitoramento da Softnew: heartbeat (online/offline no painel)
/// e envio do pacote de suporte. Token e id da máquina ficam em fleet.json no perfil.
/// </summary>
public sealed class FleetService : IFleetService
{
    public const long MaxPackageBytes = 20L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPlatformCapabilities _capabilities;
    private readonly JobQueueService _jobs;
    private readonly ILogger<FleetService> _logger;
    private readonly SoftPrintFeatureOptions _options;
    private readonly string _path;
    private readonly object _gate = new();

    private FleetFile _file;
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _lastSuccessAt;
    private bool? _lastOk;
    private string? _lastMessage;
    private string? _empresa;

    public FleetService(
        IHttpClientFactory httpClientFactory,
        IAppPaths paths,
        IOptions<SoftPrintFeatureOptions> options,
        IPlatformCapabilities capabilities,
        JobQueueService jobs,
        ILogger<FleetService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _capabilities = capabilities;
        _jobs = jobs;
        _logger = logger;
        _options = options.Value;
        _path = Path.Combine(paths.ConfigRoot, "fleet.json");
        _file = Load();
    }

    private string EnvToken => _options.FleetToken?.Trim() ?? "";

    private string EffectiveToken
    {
        get
        {
            lock (_gate)
                return string.IsNullOrWhiteSpace(_file.Token) ? EnvToken : _file.Token!.Trim();
        }
    }

    public FleetStatus GetStatus()
    {
        lock (_gate)
        {
            var token = EffectiveToken;
            return new FleetStatus(
                Configured: token.Length > 0,
                TokenFromEnv: string.IsNullOrWhiteSpace(_file.Token) && EnvToken.Length > 0,
                TokenHint: Hint(token),
                MachineId: _file.MachineId!,
                Hostname: Environment.MachineName,
                Url: BaseUrl(),
                HeartbeatSeconds: HeartbeatSeconds(_options),
                LastAttemptAt: _lastAttemptAt,
                LastSuccessAt: _lastSuccessAt,
                LastOk: _lastOk,
                LastMessage: _lastMessage,
                Empresa: _empresa);
        }
    }

    public void SetToken(string? token)
    {
        lock (_gate)
        {
            _file = _file with { Token = token?.Trim() ?? "" };
            Save();
            _lastOk = null;
            _lastMessage = null;
            _empresa = null;
        }
    }

    public static int HeartbeatSeconds(SoftPrintFeatureOptions options) =>
        Math.Clamp(options.FleetHeartbeatSeconds, 15, 600);

    public async Task<FleetSendResult> SendHeartbeatAsync(string kind = "heartbeat", CancellationToken cancellationToken = default)
    {
        var token = EffectiveToken;
        if (token.Length == 0)
            return new FleetSendResult(false, "Token do monitoramento não configurado.");

        var (pending, processing, lastError) = QueueSnapshot();
        var payload = new
        {
            kind = kind == "stopping" ? "stopping" : "heartbeat",
            machineId = MachineId(),
            hostname = Environment.MachineName,
            appVersion = SoftPrintVersion.Current,
            platform = _capabilities.Platform,
            os = RuntimeInformation.OSDescription,
            printingBackend = _capabilities.PrintingBackend,
            pending,
            processing,
            lastError
        };

        MarkAttempt();
        try
        {
            using var client = _httpClientFactory.CreateClient("softprint-fleet");
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl() + "/api/softprint/agent/heartbeat")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            AddHeaders(request, token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Finish(response.StatusCode, body, okMessage: "Conectado ao monitoramento Softnew.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Sem conexão com o monitoramento: " + ex.Message);
        }
    }

    public async Task<FleetSendResult> UploadSupportPackageAsync(string zipPath, string? note, CancellationToken cancellationToken = default)
    {
        var token = EffectiveToken;
        if (token.Length == 0)
            return new FleetSendResult(false, "Token do monitoramento não configurado.");
        var info = new FileInfo(zipPath);
        if (!info.Exists)
            return new FleetSendResult(false, "Pacote não encontrado.");
        if (info.Length > MaxPackageBytes)
            return new FleetSendResult(false,
                $"Pacote com {info.Length / 1048576.0:0.0} MB passa do limite de {MaxPackageBytes / 1048576} MB. Envie o arquivo manualmente.");

        try
        {
            using var client = _httpClientFactory.CreateClient("softprint-fleet-upload");
            using var stream = File.OpenRead(zipPath);
            using var form = new MultipartFormDataContent();
            var file = new StreamContent(stream);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "file", info.Name);
            form.Add(new StringContent(MachineId()), "machineId");
            form.Add(new StringContent(Environment.MachineName), "hostname");
            form.Add(new StringContent(SoftPrintVersion.Current), "appVersion");
            if (!string.IsNullOrWhiteSpace(note))
                form.Add(new StringContent(note!.Trim().Length > 255 ? note.Trim()[..255] : note.Trim()), "note");

            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl() + "/api/softprint/agent/support-packages")
            {
                Content = form
            };
            AddHeaders(request, token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return new FleetSendResult(true, "Pacote enviado ao suporte Softnew.", (int)response.StatusCode);
            return new FleetSendResult(false, "O servidor recusou o pacote: " + ErrorMessage(body, response.StatusCode),
                (int)response.StatusCode);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Falha ao enviar pacote de suporte");
            return new FleetSendResult(false, "Não foi possível enviar o pacote: " + ex.Message);
        }
    }

    private FleetSendResult Finish(HttpStatusCode status, string body, string okMessage)
    {
        if ((int)status is >= 200 and < 300)
        {
            string? empresa = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("empresa", out var emp) &&
                    emp.ValueKind == JsonValueKind.String)
                    empresa = emp.GetString();
            }
            catch (JsonException) { /* resposta sem JSON: segue como sucesso */ }

            lock (_gate)
            {
                _lastOk = true;
                _lastSuccessAt = DateTimeOffset.Now;
                _lastMessage = okMessage;
                _empresa = empresa ?? _empresa;
            }
            return new FleetSendResult(true, empresa is null ? okMessage : $"{okMessage} Empresa: {empresa}.", (int)status);
        }

        var message = status == HttpStatusCode.Unauthorized
            ? "Token recusado pelo servidor. Confira o token da empresa no painel."
            : "O servidor respondeu " + (int)status + ": " + ErrorMessage(body, status);
        var result = Fail(message);
        return result with { StatusCode = (int)status };
    }

    private FleetSendResult Fail(string message)
    {
        lock (_gate)
        {
            _lastOk = false;
            _lastMessage = message;
        }
        _logger.LogDebug("Heartbeat do monitoramento falhou: {Message}", message);
        return new FleetSendResult(false, message);
    }

    private void MarkAttempt()
    {
        lock (_gate) _lastAttemptAt = DateTimeOffset.Now;
    }

    private static string ErrorMessage(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? status.ToString();
        }
        catch (JsonException) { /* corpo não é JSON */ }
        return status.ToString();
    }

    private static void AddHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.TryAddWithoutValidation("X-SoftPrint-Token", token);
        request.Headers.TryAddWithoutValidation("User-Agent", "SoftPrint/" + SoftPrintVersion.Current);
    }

    private (int pending, int processing, string? lastError) QueueSnapshot()
    {
        try
        {
            var all = _jobs.List();
            var lastError = all
                .Where(j => j.Status == JobStatus.Uncertain)
                .OrderByDescending(j => j.FinishedAt)
                .Select(j => j.Error)
                .FirstOrDefault();
            if (lastError is { Length: > 240 }) lastError = lastError[..240];
            return (all.Count(j => j.Status == JobStatus.Pending), all.Count(j => j.Status == JobStatus.Processing), lastError);
        }
        catch
        {
            return (0, 0, null);
        }
    }

    private string BaseUrl()
    {
        var url = _options.FleetUrl?.Trim();
        return string.IsNullOrEmpty(url) ? SoftPrintFeatureOptions.DefaultFleetUrl : url!.TrimEnd('/');
    }

    private string MachineId()
    {
        lock (_gate) return _file.MachineId!;
    }

    private static string Hint(string token) =>
        token.Length <= 10 ? (token.Length == 0 ? "" : "••••") : token[..6] + "…" + token[^4..];

    private FleetFile Load()
    {
        FleetFile? loaded = null;
        try
        {
            if (File.Exists(_path))
                loaded = JsonSerializer.Deserialize<FleetFile>(File.ReadAllText(_path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "fleet.json inválido; um novo será criado");
        }

        loaded ??= new FleetFile();
        if (string.IsNullOrWhiteSpace(loaded.MachineId))
        {
            loaded = loaded with { MachineId = Guid.NewGuid().ToString("N") };
            _file = loaded;
            try { Save(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Não foi possível gravar fleet.json"); }
        }
        return loaded;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        DurableJsonFile.Write(_path, JsonSerializer.Serialize(_file, JsonOptions));
    }

    private sealed record FleetFile
    {
        public string? Token { get; init; }
        public string? MachineId { get; init; }
    }
}
