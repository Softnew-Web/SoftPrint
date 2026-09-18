using System.Security.Cryptography;
using System.Text;
using AutoPrint.Application.Abstractions;

namespace AutoPrint.Infrastructure.Auth;

/// <summary>Middleware: autenticação por chave de API com comparação em tempo constante.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IApiKeyProvider apiKeyProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var path = context.Request.Path.Value ?? "";

        // Dashboard web, estáticos e favicon públicos (API continua protegida).
        if (HttpMethods.IsGet(context.Request.Method) &&
            (path.Equals("/dashboard", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)))
        {
            // Evita WebView2 ficar com JS antigo em cache.
            if (path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/dashboard", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                context.Response.Headers["Pragma"] = "no-cache";
            }
            await next(context);
            return;
        }

        var provided = Encoding.UTF8.GetBytes(context.Request.Headers["X-AutoPrint-Key"].ToString());
        var expected = Encoding.UTF8.GetBytes(apiKeyProvider.ApiKey);

        if (provided.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
