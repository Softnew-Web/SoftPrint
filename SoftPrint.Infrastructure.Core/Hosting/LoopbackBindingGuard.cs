using SoftPrint.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SoftPrint.Infrastructure.Hosting;

/// <summary>Garante bind em loopback salvo opt-in explícito (AllowNonLoopbackBinding).</summary>
public static class LoopbackBindingGuard
{
    public static void Enforce(WebApplicationBuilder builder, ILogger? log = null)
    {
        var allow = builder.Configuration.GetValue("SoftPrint:AllowNonLoopbackBinding", false)
                    || builder.Configuration.GetValue("ALLOW_NON_LOOPBACK_BINDING", false);
        if (allow) return;

        var urls = builder.Configuration["Urls"]
                   ?? builder.Configuration["ASPNETCORE_URLS"]
                   ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
                   ?? "http://127.0.0.1:5178";

        if (!NeedsRewrite(urls)) return;

        var rewritten = RewriteToLoopback(urls);
        log?.LogWarning(
            "URLS '{Original}' não é só loopback. SoftPrint vai usar '{Rewritten}'. " +
            "Para expor na rede, defina SoftPrint:AllowNonLoopbackBinding=true (risco de segurança).",
            urls, rewritten);
        builder.Configuration["Urls"] = rewritten;
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", rewritten);
        builder.WebHost.UseSetting("urls", rewritten);
    }

    internal static bool NeedsRewrite(string urls)
    {
        foreach (var part in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains("://0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
                part.Contains("://+", StringComparison.OrdinalIgnoreCase) ||
                part.Contains("://*", StringComparison.OrdinalIgnoreCase) ||
                part.Contains("://[::]", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!Uri.TryCreate(part, UriKind.Absolute, out var uri))
                continue;
            if (uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]")
                continue;
            return true;
        }

        return false;
    }

    internal static string RewriteToLoopback(string urls)
    {
        var parts = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rewritten = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out var uri))
            {
                rewritten.Add("http://127.0.0.1:5178");
                continue;
            }

            var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 5178) : uri.Port;
            rewritten.Add($"{uri.Scheme}://127.0.0.1:{port}");
        }

        return rewritten.Count == 0 ? "http://127.0.0.1:5178" : string.Join(';', rewritten);
    }
}
