using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Api.Endpoints;

public static class WebDashboardEndpoints
{
    public static IEndpointRouteBuilder MapWebDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/dashboard"));
        app.MapGet("/dashboard", (IApiKeyProvider keys, IHostEnvironment env) =>
        {
            var path = Path.Combine(env.ContentRootPath, "wwwroot", "dashboard.html");
            if (!File.Exists(path))
                return Results.Content("<h1>dashboard.html não encontrado</h1>", "text/html; charset=utf-8");

            var html = File.ReadAllText(path)
                // Não usar HtmlEncode na chave: quebraria o JS se tivesse caracteres especiais.
                .Replace("{{API_KEY}}", keys.ApiKey.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", ""), StringComparison.Ordinal)
                .Replace("{{APP_VERSION}}", SoftPrintVersion.Current, StringComparison.Ordinal);
            return Results.Content(html, "text/html; charset=utf-8");
        });
        return app;
    }
}
