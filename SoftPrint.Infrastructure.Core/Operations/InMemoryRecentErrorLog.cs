using SoftPrint.Application.Abstractions;

namespace SoftPrint.Infrastructure.Operations;

public sealed class InMemoryRecentErrorLog : IRecentErrorLog
{
    private const int Cap = 50;
    private readonly object _gate = new();
    private readonly LinkedList<RecentError> _list = new();

    public void Record(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new RecentError(DateTimeOffset.UtcNow, source, message);
        lock (_gate)
        {
            _list.AddFirst(entry);
            while (_list.Count > Cap)
                _list.RemoveLast();
        }
    }

    public IReadOnlyList<RecentError> GetRecent(int max = 30)
    {
        lock (_gate)
            return _list.Take(Math.Clamp(max, 1, Cap)).ToList();
    }

    public void Clear()
    {
        lock (_gate)
            _list.Clear();
    }
}
