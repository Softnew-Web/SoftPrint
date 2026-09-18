using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SoftPrint.Infrastructure.Updates;

public sealed class GitHubReleaseUpdateChecker : IUpdateChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<SoftPrintFeatureOptions> _options;
    private readonly ILogger<GitHubReleaseUpdateChecker> _logger;
    private readonly object _gate = new();
    private UpdateCheckResult? _cached;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    public GitHubReleaseUpdateChecker(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<SoftPrintFeatureOptions> options,
        ILogger<GitHubReleaseUpdateChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.CurrentValue;
        var current = SoftPrintVersion.Current;
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_cached is not null && now < _cachedUntil)
                return _cached;
        }

        if (!opts.UpdateCheckEnabled)
        {
            return Cache(new UpdateCheckResult(
                current, null, false, false, null, null, null, null, now));
        }

        var owner = opts.UpdateGitHubOwner.Trim();
        var repo = opts.UpdateGitHubRepo.Trim();
        if (owner.Length == 0 || repo.Length == 0)
        {
            return Cache(new UpdateCheckResult(
                current, null, false, false, null, null, null,
                "Repositório GitHub de atualização não configurado.", now));
        }

        try
        {
            var client = _httpClientFactory.CreateClient("softprint-update");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"SoftPrint/{current}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var token = FirstNonEmpty(
                opts.UpdateGitHubToken,
                Environment.GetEnvironmentVariable("SOFTPRINT_GITHUB_TOKEN"),
                Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Cache(new UpdateCheckResult(
                    current, null, false, false, null, null, null,
                    "Nenhum release publicado ainda neste repositório.", now));
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Falha ao consultar releases GitHub: {Status} {Body}", (int)response.StatusCode, Truncate(body));
                return Cache(new UpdateCheckResult(
                    current, null, false, false, null, null, null,
                    $"GitHub respondeu {(int)response.StatusCode}.", now));
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(JsonOptions, cancellationToken)
                          .ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return Cache(new UpdateCheckResult(
                    current, null, false, false, null, null, null,
                    "Release GitHub inválido.", now));
            }

            var latest = SoftPrintVersionCompare.Normalize(release.TagName);
            var updateAvailable = SoftPrintVersionCompare.IsNewer(latest, current);
            var mandatory = updateAvailable && (
                opts.UpdateAlwaysMandatory ||
                ContainsMandatoryMarker(release.Name) ||
                ContainsMandatoryMarker(release.Body));

            var assetName = opts.UpdateAssetName.Trim();
            var asset = release.Assets?
                .FirstOrDefault(a =>
                    (string.IsNullOrWhiteSpace(assetName)
                        ? a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
                        : string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
                    && (!string.IsNullOrWhiteSpace(a.Url) || !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl)));

            // Em repos privados, browser_download_url retorna 404.
            // A URL da API (assets/{id}) com Accept: application/octet-stream funciona com o token.
            var download = !string.IsNullOrWhiteSpace(asset?.Url)
                ? asset!.Url
                : asset?.BrowserDownloadUrl ?? release.HtmlUrl;

            return Cache(new UpdateCheckResult(
                current,
                latest,
                updateAvailable,
                mandatory,
                updateAvailable ? download : null,
                release.HtmlUrl,
                release.Body,
                null,
                now));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao verificar atualização no GitHub");
            return Cache(new UpdateCheckResult(
                current, null, false, false, null, null, null, ex.Message, now));
        }
    }

    public UpdateCheckResult? TryGetCached()
    {
        lock (_gate)
            return _cached;
    }

    /// <summary>Força nova consulta na próxima verificação (ex.: após falha de download).</summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
            _cachedUntil = DateTimeOffset.MinValue;
        }
    }

    private UpdateCheckResult Cache(UpdateCheckResult result)
    {
        var minutes = Math.Clamp(_options.CurrentValue.UpdateCacheMinutes, 5, 24 * 60);
        lock (_gate)
        {
            _cached = result;
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
            return result;
        }
    }

    private static bool ContainsMandatoryMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("[mandatory]", StringComparison.OrdinalIgnoreCase)
               || text.Contains("softprint:mandatory", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200];

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
