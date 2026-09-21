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
            var current = settings.Current;
            if (!current.TelemetryEnabled) return;

            var url = current.TelemetryUrl?.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;

            var payload = new
            {
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

            using var client = httpClientFactory.CreateClient("softprint-telemetry");
            using var response = await client.PostAsJsonAsync(url, payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Telemetry POST retornou {Status} para pedido {JobId}",
                    (int)response.StatusCode,
                    job.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Telemetry ignorada/falhou para pedido {JobId}", job.Id);
        }
    }
}
