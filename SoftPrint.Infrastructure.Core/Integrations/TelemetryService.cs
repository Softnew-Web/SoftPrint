using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;
using Microsoft.Extensions.Logging;

namespace SoftPrint.Infrastructure.Integrations;

public sealed class TelemetryService(
    IHttpClientFactory httpClientFactory,
    ISystemSettingsRepository settings,
    IPlatformCapabilities capabilities,
    ILogger<TelemetryService> logger) : ITelemetryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task ReportPrintFailureAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryGetEndpoint(out var url)) return;

            var payload = new
            {
                kind = "print-failure",
                appVersion = SoftPrintVersion.Current,
                platform = capabilities.Platform,
                os = RuntimeInformation.OSDescription,
                printingBackend = capabilities.PrintingBackend,
                jobType = job.JobType,
                contentKind = job.ContentKind.ToWire(),
                status = job.Status.ToWire(),
                error = job.Error,
                errorReason = job.ErrorReason,
                errorWhere = job.ErrorWhere
            };

            await PostAsync(url, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Telemetry ignorada/falhou para pedido {JobId}", job.Id);
        }
    }

    public async Task ReportHeartbeatAsync(
        int pending,
        int processing,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryGetEndpoint(out var url)) return;

            var payload = new
            {
                kind = "heartbeat",
                appVersion = SoftPrintVersion.Current,
                platform = capabilities.Platform,
                os = RuntimeInformation.OSDescription,
                printingBackend = capabilities.PrintingBackend,
                pending,
                processing,
                lastError = Truncate(lastError, 240),
                at = DateTimeOffset.UtcNow
            };

            await PostAsync(url, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Telemetry heartbeat falhou");
        }
    }

    private bool TryGetEndpoint(out string url)
    {
        url = "";
        var current = settings.Current;
        if (!current.TelemetryEnabled) return false;
        var trimmed = current.TelemetryUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        url = trimmed;
        return true;
    }

    private async Task PostAsync(string url, object payload, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("softprint-telemetry");
        using var response = await client.PostAsJsonAsync(url, payload, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Telemetry POST retornou {Status}", (int)response.StatusCode);
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}
