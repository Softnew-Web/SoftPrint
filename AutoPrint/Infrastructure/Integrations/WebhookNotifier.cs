using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoPrint.Application;
using AutoPrint.Application.Abstractions;
using AutoPrint.Domain;
using Microsoft.Extensions.Options;

namespace AutoPrint.Infrastructure.Integrations;

public sealed class WebhookNotifier(
    IHttpClientFactory httpClientFactory,
    IEventLogStore eventLogs,
    IWebhookRetryQueue retryQueue,
    IOptions<AutoPrintFeatureOptions> options,
    ILogger<WebhookNotifier> logger) : IWebhookNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task NotifyFinishedAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        var payload = ToEvent(job, delivery: null, detail: null);
        var url = options.Value.WebhookUrl?.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            eventLogs.Write(payload with { Delivery = "file", DeliveryDetail = "WEBHOOK_URL vazio" });
            return;
        }

        var (ok, error) = await TryPostAsync(url, payload, cancellationToken);
        if (ok) return;

        logger.LogWarning("Webhook falhou para {JobId}: {Error}", job.Id, error);
        var failed = payload with { Delivery = "webhook-failed", DeliveryDetail = error };
        eventLogs.Write(failed);
        retryQueue.Enqueue(failed, error ?? "falha no webhook");
    }

    internal async Task<(bool Ok, string? Error)> TryPostAsync(
        string url, JobFinishedEventLog payload, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("autoprinthook");
            client.Timeout = TimeSpan.FromMilliseconds(Math.Clamp(options.Value.WebhookTimeoutMs, 500, 60_000));
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var secret = options.Value.WebhookSecret?.Trim();
            if (!string.IsNullOrWhiteSpace(secret))
            {
                var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(json));
                request.Headers.TryAddWithoutValidation("X-AutoPrint-Signature", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return (true, null);
            return (false, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static JobFinishedEventLog ToEvent(PrintJob job, string? delivery, string? detail) => new(
        EventType: "job.finished",
        At: DateTimeOffset.Now,
        Id: job.Id,
        Reference: job.Reference,
        JobType: job.JobType,
        ContentKind: job.ContentKind.ToWire(),
        Status: job.Status.ToWire(),
        PrinterName: job.PrinterName,
        Error: job.Error,
        ErrorReason: job.ErrorReason,
        ErrorWhere: job.ErrorWhere,
        FinishedAt: job.FinishedAt,
        Steps: job.Steps.Select(s => new { s.At, s.Stage, s.Where, s.Message, s.Detail, s.IsError }).ToArray(),
        Delivery: delivery,
        DeliveryDetail: detail);
}

public sealed class WebhookRetryQueue(
    IHostEnvironment environment,
    IOptions<AutoPrintFeatureOptions> options) : IWebhookRetryQueue
{
    private readonly object _gate = new();
    private readonly List<WebhookRetryItem> _items = [];
    private readonly string _path = Path.Combine(environment.ContentRootPath, "data", "webhook-retry.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public WebhookRetryQueue Init()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<WebhookRetryItem>>(File.ReadAllText(_path), JsonOptions);
                    if (loaded is not null) _items.AddRange(loaded);
                }
                catch { /* ignore corrupt queue */ }
            }
        }
        return this;
    }

    public void Enqueue(JobFinishedEventLog entry, string reason)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Id == entry.Id);
            _items.Add(new WebhookRetryItem(
                entry.Id, DateTimeOffset.UtcNow, 0, reason, entry,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, options.Value.WebhookRetrySeconds))));
            Save();
        }
    }

    public IReadOnlyList<WebhookRetryItem> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public List<WebhookRetryItem> Due(DateTimeOffset now)
    {
        lock (_gate) return _items.Where(i => i.NextAttemptAt <= now).ToList();
    }

    public void MarkSuccess(Guid id)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Id == id);
            Save();
        }
    }

    public void MarkFailure(Guid id, string reason)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(i => i.Id == id);
            if (index < 0) return;
            var item = _items[index];
            var attempts = item.Attempts + 1;
            if (attempts >= Math.Max(1, options.Value.WebhookMaxRetries))
            {
                _items.RemoveAt(index);
            }
            else
            {
                var delay = Math.Min(3600, Math.Max(5, options.Value.WebhookRetrySeconds) * (1 << Math.Min(attempts, 6)));
                _items[index] = item with
                {
                    Attempts = attempts,
                    Reason = reason,
                    NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delay)
                };
            }
            Save();
        }
    }

    private void Save()
    {
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_items, JsonOptions));
        File.Move(_path + ".tmp", _path, true);
    }
}

public sealed class WebhookRetryWorker(
    WebhookRetryQueue queue,
    WebhookNotifier notifier,
    IOptions<AutoPrintFeatureOptions> options,
    ILogger<WebhookRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var url = options.Value.WebhookUrl?.Trim();
            if (!string.IsNullOrWhiteSpace(url))
            {
                foreach (var item in queue.Due(DateTimeOffset.UtcNow))
                {
                    var (ok, error) = await notifier.TryPostAsync(url, item.Payload, stoppingToken);
                    if (ok)
                    {
                        queue.MarkSuccess(item.Id);
                        logger.LogInformation("Webhook retry OK para job {JobId}", item.Id);
                    }
                    else
                    {
                        queue.MarkFailure(item.Id, error ?? "falha");
                        logger.LogWarning("Webhook retry falhou ({Attempt}) job {JobId}: {Error}",
                            item.Attempts + 1, item.Id, error);
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.WebhookRetrySeconds)), stoppingToken);
        }
    }
}
