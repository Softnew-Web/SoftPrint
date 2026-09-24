using System.Net;
using System.Security.Cryptography;
using System.Text;
using SoftPrint.Application;
using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Auth;

/// <summary>
/// Autenticação por cabeçalho X-SoftPrint-Key (integradores) ou cookie HttpOnly SoftPrint.Key (painel local).
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IApiKeyProvider apiKeyProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var path = context.Request.Path.Value ?? "";

        if (HttpMethods.IsGet(context.Request.Method) &&
            (path.Equals("/dashboard", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)))
        {
            if (path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/dashboard", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
                context.Response.Headers["Pragma"] = "no-cache";
            }

            if (path.Equals("/dashboard", StringComparison.OrdinalIgnoreCase) && IsLoopback(context))
                AppendSessionCookie(context.Response, apiKeyProvider.ApiKey);

            await next(context);
            return;
        }

        if (!IsAuthorized(context, apiKeyProvider.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    public static bool IsAuthorized(HttpContext context, string expectedKey)
    {
        var providedValue = context.Request.Headers["X-SoftPrint-Key"].ToString();
        if (string.IsNullOrEmpty(providedValue))
            providedValue = context.Request.Headers["X-AutoPrint-Key"].ToString();
        if (string.IsNullOrEmpty(providedValue))
            providedValue = context.Request.Cookies[SoftPrintAuthDefaults.CookieName] ?? "";

        var provided = Encoding.UTF8.GetBytes(providedValue);
        var expected = Encoding.UTF8.GetBytes(expectedKey);
        return provided.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    public static void AppendSessionCookie(HttpResponse response, string apiKey)
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

    public static bool IsLoopback(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return true;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4()))
            return true;
        return false;
    }
}
