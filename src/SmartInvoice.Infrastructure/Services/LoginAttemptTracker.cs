using System.Collections.Concurrent;
using SmartInvoice.Application.Services;

namespace SmartInvoice.Infrastructure.Services;

public class LoginAttemptTracker : ILoginAttemptTracker
{
    private const int BlockThreshold = 3;
    private readonly ConcurrentDictionary<string, int> _failureCountByUsername = new(StringComparer.OrdinalIgnoreCase);

    public int GetFailureCount(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return 0;
        return _failureCountByUsername.TryGetValue(username.Trim(), out var count) ? count : 0;
    }

    public void RecordFailure(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var key = username.Trim();
        _failureCountByUsername.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    public bool IsBlocked(string username)
    {
        return GetFailureCount(username) >= BlockThreshold;
    }

    public void Reset(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        _failureCountByUsername.TryRemove(username.Trim(), out _);
    }
}
