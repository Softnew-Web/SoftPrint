using AutoPrint.Domain;

namespace AutoPrint.Infrastructure.Configuration;

public static class JobStatusTypeCatalogFactory
{
    public static JobStatusTypeCatalog FromConfiguration(IConfiguration configuration)
    {
        var map = JobStatusTypeCatalog.CreateDefaults();
        foreach (var status in Enum.GetValues<JobStatus>())
        {
            var section = configuration.GetSection($"AutoPrint:Statuses:{status}");
            var wire = section["Wire"];
            var label = section["Label"];
            if (string.IsNullOrWhiteSpace(wire) && string.IsNullOrWhiteSpace(label))
                continue;

            var current = map[status];
            map[status] = new JobStatusType(
                string.IsNullOrWhiteSpace(wire) ? current.Wire : wire.Trim(),
                string.IsNullOrWhiteSpace(label) ? current.Label : label.Trim());
        }

        return new JobStatusTypeCatalog(map);
    }
}
