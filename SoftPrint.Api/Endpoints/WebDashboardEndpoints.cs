using System.Net;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Api.Endpoints;

public static class WebDashboardEndpoints
{
    public static IEndpointRouteBuilder MapWebDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/dashboard"));
        app.MapGet("/dashboard", (IHostEnvironment env) =>
        {
            var path = Path.Combine(env.ContentRootPath, "wwwroot", "dashboard.html");
            if (!File.Exists(path))
                return Results.Content("<h1>dashboard.html não encontrado</h1>", "text/html; charset=utf-8");

            var html = File.ReadAllText(path)
                .Replace("{{API_KEY}}", "", StringComparison.Ordinal)
                .Replace("{{APP_VERSION}}", SoftPrintVersion.Current, StringComparison.Ordinal);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/api/connect/key", (IApiKeyProvider keys, HttpContext http) =>
        {
            if (!IsLoopback(http) &&
                string.IsNullOrEmpty(http.Request.Headers["X-SoftPrint-Key"].ToString()))
            {
                return Results.Json(new { error = "Revelar a chave só é permitido em localhost ou com header." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new
            {
                apiKey = keys.ApiKey,
                hint = keys.ApiKey.Length >= 4
                    ? keys.ApiKey[..2] + "…" + keys.ApiKey[^2..]
                    : "(definida)"
            });
        });

        app.MapPost("/api/connect/rotate-key", (IApiKeyProvider keys, HttpContext http) =>
        {
            var next = keys.Rotate();
            if (IsLoopback(http))
                AppendSessionCookie(http.Response, next);

            return Results.Ok(new
            {
                rotated = true,
                hint = next.Length >= 4 ? next[..2] + "…" + next[^2..] : "(definida)",
                message = "Nova chave gerada. Atualize integrações que usam a chave antiga."
            });
        });

        return app;
    }

    private static void AppendSessionCookie(HttpResponse response, string apiKey)
    {
        response.Cookies.Append(SoftPrintAuthDefaults.CookieName, apiKey, new CookieOptions
        {
            HttpOnly = true,
            Secure = false,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true
        });
    }

    private static bool IsLoopback(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return true;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()))
            return true;
        return false;
    }
}
