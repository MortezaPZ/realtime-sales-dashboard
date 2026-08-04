using System.Collections.Concurrent;
using RealtimeBi.Api.Domain;

namespace RealtimeBi.Api.Services;

public interface IAggregator
{
    void Ingest(SalesEvent salesEvent);
    DashboardSnapshot Snapshot();
    int Count { get; }
}

/// <summary>
/// Keeps the last N minutes of sales in memory and projects them into a
/// dashboard snapshot on demand.
///
/// Ingest is called from the producer thread while Snapshot is called from the
/// broadcast loop and from HTTP requests, so the buffer is guarded by a
/// reader/writer lock: many concurrent snapshots, one writer at a time.
/// </summary>
public sealed class SlidingWindowAggregator : IAggregator, IDisposable
{
    private readonly LinkedList<SalesEvent> _events = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly TimeSpan _window;
    private readonly TimeSpan _bucketSize;
    private readonly TimeProvider _time;
    private readonly int _maxEvents;

    public SlidingWindowAggregator(
        TimeSpan? window = null,
        TimeSpan? bucketSize = null,
        TimeProvider? timeProvider = null,
        int maxEvents = 200_000)
    {
        _window = window ?? TimeSpan.FromMinutes(5);
        _bucketSize = bucketSize ?? TimeSpan.FromSeconds(30);

        if (_bucketSize <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bucketSize), "Bucket size must be positive.");
        if (_window < _bucketSize)
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be at least one bucket long.");

        _time = timeProvider ?? TimeProvider.System;
        _maxEvents = maxEvents;
    }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _events.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void Ingest(SalesEvent salesEvent)
    {
        ArgumentNullException.ThrowIfNull(salesEvent);

        var errors = salesEvent.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(salesEvent));

        _lock.EnterWriteLock();
        try
        {
            // Events usually arrive in order, so appending and then trimming from
            // the front keeps this O(1) amortised. Out-of-order arrivals still land
            // correctly because Snapshot filters by timestamp, not by position.
            _events.AddLast(salesEvent);
            Evict(_time.GetUtcNow());
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public DashboardSnapshot Snapshot()
    {
        var now = _time.GetUtcNow();
        var cutoff = now - _window;

        SalesEvent[] live;
        _lock.EnterReadLock();
        try
        {
            live = _events.Where(e => e.OccurredAt > cutoff).ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (live.Length == 0)
            return DashboardSnapshot.Empty((int)_window.TotalMinutes);

        var revenue = live.Sum(e => e.Amount);

        return new DashboardSnapshot
        {
            GeneratedAt = now,
            WindowMinutes = (int)_window.TotalMinutes,
            Revenue = decimal.Round(revenue, 2),
            Orders = live.Length,
            AverageOrderValue = decimal.Round(revenue / live.Length, 2),
            ByRegion = Group(live, e => e.Region),
            ByChannel = Group(live, e => e.Channel),
            Timeline = BuildTimeline(live, now),
        };
    }

    private static IReadOnlyList<Breakdown> Group(
        IEnumerable<SalesEvent> events, Func<SalesEvent, string> selector) =>
        events
            .GroupBy(selector)
            .Select(g => new Breakdown
            {
                Key = g.Key,
                Revenue = decimal.Round(g.Sum(e => e.Amount), 2),
                Orders = g.Count(),
            })
            .OrderByDescending(b => b.Revenue)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToArray();

    private IReadOnlyList<TimeBucket> BuildTimeline(
        IReadOnlyCollection<SalesEvent> events, DateTimeOffset now)
    {
        var bucketCount = (int)Math.Ceiling(_window / _bucketSize);
        var newest = FloorToBucket(now);

        // Pre-seed every bucket so a quiet period renders as a zero rather than
        // a gap the chart has to interpolate across.
        var totals = new Dictionary<DateTimeOffset, (decimal Revenue, int Orders)>(bucketCount);
        for (var i = bucketCount - 1; i >= 0; i--)
            totals[newest - i * _bucketSize] = (0m, 0);

        foreach (var salesEvent in events)
        {
            var bucket = FloorToBucket(salesEvent.OccurredAt);
            if (totals.TryGetValue(bucket, out var current))
                totals[bucket] = (current.Revenue + salesEvent.Amount, current.Orders + 1);
        }

        return totals
            .OrderBy(pair => pair.Key)
            .Select(pair => new TimeBucket
            {
                StartsAt = pair.Key,
                Revenue = decimal.Round(pair.Value.Revenue, 2),
                Orders = pair.Value.Orders,
            })
            .ToArray();
    }

    private DateTimeOffset FloorToBucket(DateTimeOffset moment)
    {
        var ticks = moment.UtcTicks - (moment.UtcTicks % _bucketSize.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>Drops events that have aged out. Caller must hold the write lock.</summary>
    private void Evict(DateTimeOffset now)
    {
        var cutoff = now - _window;

        while (_events.First is { } first && first.Value.OccurredAt <= cutoff)
            _events.RemoveFirst();

        // Safety valve: a burst of future-dated events would otherwise never be
        // evicted by time alone and could grow the buffer without limit.
        while (_events.Count > _maxEvents)
            _events.RemoveFirst();
    }

    public void Dispose() => _lock.Dispose();
}
