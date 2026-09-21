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
                Environment.GetEnvironmentVariable("UPDATE_GITHUB_TOKEN"),
                Environment.GetEnvironmentVariable("SOFTPRINT_GITHUB_TOKEN"),
                Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Repo privado sem token também devolve 404.
                var hint = string.IsNullOrWhiteSpace(token)
                    ? "Repositório privado ou sem releases. Configure UPDATE_GITHUB_TOKEN (ou torne o repo público)."
                    : "Nenhum release publicado ainda neste repositório.";
                return CacheError(new UpdateCheckResult(
                    current, null, false, false, null, null, null, hint, now));
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Falha ao consultar releases GitHub: {Status} {Body}", (int)response.StatusCode, Truncate(body));
                return CacheError(new UpdateCheckResult(
                    current, null, false, false, null, null, null,
                    $"GitHub respondeu {(int)response.StatusCode}.", now));
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(JsonOptions, cancellationToken)
                          .ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return CacheError(new UpdateCheckResult(
                    current, null, false, false, null, null, null,
                    "Release GitHub inválido.", now));
            }

            var latest = SoftPrintVersionCompare.Normalize(release.TagName);
            if (!SoftPrintVersionCompare.IsParseable(latest))
            {
                return CacheError(new UpdateCheckResult(
                    current, null, false, false, null, null, null,
                    $"Tag de release inválida: '{release.TagName}'.", now));
            }

            var updateAvailable = SoftPrintVersionCompare.IsNewer(latest, current);
            var mandatory = updateAvailable && (
                opts.UpdateAlwaysMandatory ||
                ContainsMandatoryMarker(release.Name) ||
                ContainsMandatoryMarker(release.Body));

            var asset = SelectAsset(release.Assets, opts.UpdateAssetName, current);

            // Preferir URL da API (assets/{id}); browser_download_url falha em repos privados.
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
                now,
                updateAvailable ? asset?.Name : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erro ao verificar atualização no GitHub");
            return CacheError(new UpdateCheckResult(
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

    private UpdateCheckResult Cache(UpdateCheckResult result) =>
        CacheForMinutes(result, Math.Clamp(_options.CurrentValue.UpdateCacheMinutes, 5, 24 * 60));

    private UpdateCheckResult CacheError(UpdateCheckResult result) =>
        CacheForMinutes(result, 2);

    private UpdateCheckResult CacheForMinutes(UpdateCheckResult result, int minutes)
    {
        lock (_gate)
        {
            _cached = result;
            _cachedUntil = DateTimeOffset.UtcNow.AddMinutes(minutes);
            return result;
        }
    }

    internal static string PreferredZipAssetName()
    {
        var isLegacy = (Environment.ProcessPath ?? "")
            .Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        var arch = Environment.Is64BitProcess ? "x64" : "x86";
        return isLegacy
            ? $"SoftPrint-legacy-{arch}.zip"
            : $"SoftPrint-win-{arch}.zip";
    }

    internal static string PreferredDeltaAssetName(string currentVersion)
    {
        var ver = SoftPrintVersionCompare.Normalize(currentVersion);
        var isLegacy = (Environment.ProcessPath ?? "")
            .Contains("Legacy", StringComparison.OrdinalIgnoreCase);
        var arch = Environment.Is64BitProcess ? "x64" : "x86";
        return isLegacy
            ? $"SoftPrint-legacy-{arch}-from-{ver}.zip"
            : $"SoftPrint-win-{arch}-from-{ver}.zip";
    }

    private static GitHubAsset? SelectAsset(List<GitHubAsset>? assets, string updateAssetName, string currentVersion)
    {
        if (assets is null || assets.Count == 0) return null;

        static bool HasUrl(GitHubAsset a) =>
            !string.IsNullOrWhiteSpace(a.Url) || !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl);

        // 1) Delta a partir da versão instalada (só o que mudou).
        var deltaName = PreferredDeltaAssetName(currentVersion);
        var delta = assets.FirstOrDefault(a =>
            string.Equals(a.Name, deltaName, StringComparison.OrdinalIgnoreCase) && HasUrl(a));
        if (delta is not null) return delta;

        // 2) Zip completo da arquitetura.
        var preferredZip = PreferredZipAssetName();
        var zip = assets.FirstOrDefault(a =>
            string.Equals(a.Name, preferredZip, StringComparison.OrdinalIgnoreCase) && HasUrl(a));
        if (zip is not null) return zip;

        var assetName = updateAssetName.Trim();
        return assets.FirstOrDefault(a =>
            (string.IsNullOrWhiteSpace(assetName)
                ? a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
                : string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
            && HasUrl(a));
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
