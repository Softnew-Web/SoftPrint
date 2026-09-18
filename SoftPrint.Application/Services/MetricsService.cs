using SoftPrint.Application.Abstractions;
using SoftPrint.Domain;

namespace SoftPrint.Application.Services;

public sealed class MetricsService(IJobRepository jobs) : IMetricsService
{
    public MetricsSnapshot Compute()
    {
        var all = jobs.Snapshot();
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var last24 = all.Where(j => j.CreatedAt >= since).ToArray();
        var finished = all.Where(j => j.Status is JobStatus.Simulated or JobStatus.Sent or JobStatus.Uncertain).ToArray();
        var uncertain = all.Count(j => j.Status == JobStatus.Uncertain);
        var rate = finished.Length == 0 ? 0 : uncertain * 100.0 / finished.Length;
        var busiest = all.GroupBy(j => j.JobType)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        return new MetricsSnapshot(
            TotalJobs: all.Count,
            Last24Hours: last24.Length,
            JobsPerHourLast24h: Math.Round(last24.Length / 24.0, 2),
            UncertainTotal: uncertain,
            UncertainRatePercent: Math.Round(rate, 1),
            SimulatedTotal: all.Count(j => j.Status == JobStatus.Simulated),
            SentTotal: all.Count(j => j.Status == JobStatus.Sent),
            Pending: all.Count(j => j.Status == JobStatus.Pending),
            Processing: all.Count(j => j.Status == JobStatus.Processing),
            BusiestJobType: busiest);
    }
}
