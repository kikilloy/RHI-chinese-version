// MirrorHealthTracker.cs — session-scoped circuit breaker for mirror prefixes.
//
// A mirror that just failed twice in a row is not going to work for the next request either;
// skipping it for 10 minutes keeps a dead mirror from adding 15 s to every download. Nothing
// is persisted: a restart is a clean slate, which is what a "maybe it's back" mirror wants.

using System.Collections.Concurrent;

namespace RenoDXCommander.Services.Net;

public interface IMirrorHealthTracker
{
    /// <summary>True while the mirror should be skipped (2 consecutive failures, 10 min).</summary>
    bool IsCircuitOpen(string mirrorKey);
    int ConsecutiveFailures(string mirrorKey);
    void RecordSuccess(string mirrorKey);
    void RecordFailure(string mirrorKey);
    void Reset();
}

/// <inheritdoc />
public sealed class MirrorHealthTracker : IMirrorHealthTracker
{
    /// <summary>Consecutive failures before a mirror is skipped for <see cref="Cooldown"/>.</summary>
    public const int FailureThreshold = 2;

    /// <summary>How long a tripped mirror is skipped.</summary>
    public static TimeSpan Cooldown { get; } = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public MirrorHealthTracker() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>Test seam — lets the cooldown be exercised without sleeping.</summary>
    public MirrorHealthTracker(Func<DateTimeOffset> now) => _now = now;

    public bool IsCircuitOpen(string mirrorKey)
    {
        if (string.IsNullOrEmpty(mirrorKey)) return false;
        if (!_entries.TryGetValue(mirrorKey, out var entry)) return false;

        lock (entry)
        {
            if (entry.Failures < FailureThreshold) return false;
            if (_now() >= entry.OpenUntil)
            {
                // Cooldown elapsed — give it another chance, but don't pretend it's healthy.
                entry.Failures = 0;
                entry.OpenUntil = DateTimeOffset.MinValue;
                return false;
            }
            return true;
        }
    }

    public int ConsecutiveFailures(string mirrorKey)
    {
        if (string.IsNullOrEmpty(mirrorKey)) return 0;
        if (!_entries.TryGetValue(mirrorKey, out var entry)) return 0;
        lock (entry) return entry.Failures;
    }

    public void RecordSuccess(string mirrorKey)
    {
        if (string.IsNullOrEmpty(mirrorKey)) return;
        if (_entries.TryGetValue(mirrorKey, out var entry))
        {
            lock (entry)
            {
                entry.Failures = 0;
                entry.OpenUntil = DateTimeOffset.MinValue;
            }
        }
    }

    public void RecordFailure(string mirrorKey)
    {
        if (string.IsNullOrEmpty(mirrorKey)) return;
        var entry = _entries.GetOrAdd(mirrorKey, _ => new Entry());
        lock (entry)
        {
            if (_now() >= entry.OpenUntil && entry.Failures >= FailureThreshold) entry.Failures = 0;
            entry.Failures++;
            if (entry.Failures >= FailureThreshold) entry.OpenUntil = _now() + Cooldown;
        }
    }

    public void Reset() => _entries.Clear();

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset OpenUntil = DateTimeOffset.MinValue;
    }
}
