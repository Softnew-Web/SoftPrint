namespace SoftPrint.Application.Abstractions;

public interface IRecentErrorLog
{
    void Record(string source, string message);
    void Clear();
    IReadOnlyList<RecentError> GetRecent(int max = 30);
}

public sealed record RecentError(DateTimeOffset At, string Source, string Message);
