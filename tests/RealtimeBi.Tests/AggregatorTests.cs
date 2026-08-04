using RealtimeBi.Api.Domain;
using RealtimeBi.Api.Services;

namespace RealtimeBi.Tests;

public class AggregatorTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private static SalesEvent Sale(
        decimal amount,
        DateTimeOffset at,
        string region = "North",
        string channel = "Web",
        string? id = null) => new()
        {
            OrderId = id ?? $"ORD-{Guid.NewGuid():N}",
            Region = region,
            Channel = channel,
            Amount = amount,
            OccurredAt = at,
        };

    private static (SlidingWindowAggregator Aggregator, FakeTimeProvider Time) Build(
        int windowMinutes = 5, int bucketSeconds = 30)
    {
        var time = new FakeTimeProvider(Origin);
        var aggregator = new SlidingWindowAggregator(
            TimeSpan.FromMinutes(windowMinutes),
            TimeSpan.FromSeconds(bucketSeconds),
            time);
        return (aggregator, time);
    }

    [Fact]
    public void EmptyAggregatorReturnsZeroedSnapshot()
    {
        var (aggregator, _) = Build();

        var snapshot = aggregator.Snapshot();

        Assert.Equal(0m, snapshot.Revenue);
        Assert.Equal(0, snapshot.Orders);
        Assert.Empty(snapshot.ByRegion);
    }

    [Fact]
    public void RevenueAndOrdersAccumulate()
    {
        var (aggregator, _) = Build();

        aggregator.Ingest(Sale(100.50m, Origin));
        aggregator.Ingest(Sale(49.50m, Origin));

        var snapshot = aggregator.Snapshot();
        Assert.Equal(150.00m, snapshot.Revenue);
        Assert.Equal(2, snapshot.Orders);
        Assert.Equal(75.00m, snapshot.AverageOrderValue);
    }

    [Fact]
    public void BreakdownsGroupAndSortByRevenue()
    {
        var (aggregator, _) = Build();

        aggregator.Ingest(Sale(100m, Origin, region: "North"));
        aggregator.Ingest(Sale(250m, Origin, region: "South"));
        aggregator.Ingest(Sale(50m, Origin, region: "North"));

        var regions = aggregator.Snapshot().ByRegion;

        Assert.Equal("South", regions[0].Key);
        Assert.Equal(250m, regions[0].Revenue);
        Assert.Equal("North", regions[1].Key);
        Assert.Equal(150m, regions[1].Revenue);
        Assert.Equal(2, regions[1].Orders);
    }

    [Fact]
    public void ChannelBreakdownIsIndependentOfRegion()
    {
        var (aggregator, _) = Build();

        aggregator.Ingest(Sale(100m, Origin, region: "North", channel: "Web"));
        aggregator.Ingest(Sale(100m, Origin, region: "South", channel: "Web"));

        var channels = aggregator.Snapshot().ByChannel;

        Assert.Single(channels);
        Assert.Equal("Web", channels[0].Key);
        Assert.Equal(200m, channels[0].Revenue);
    }

    [Fact]
    public void AverageOrderValueIsRoundedToCents()
    {
        var (aggregator, _) = Build();

        aggregator.Ingest(Sale(10m, Origin));
        aggregator.Ingest(Sale(10m, Origin));
        aggregator.Ingest(Sale(10.01m, Origin));

        Assert.Equal(10.00m, aggregator.Snapshot().AverageOrderValue);
    }

    [Fact]
    public void EventsOlderThanTheWindowAreExcluded()
    {
        var (aggregator, time) = Build(windowMinutes: 5);

        aggregator.Ingest(Sale(100m, Origin));
        time.Advance(TimeSpan.FromMinutes(6));
        aggregator.Ingest(Sale(40m, time.GetUtcNow()));

        var snapshot = aggregator.Snapshot();

        Assert.Equal(40m, snapshot.Revenue);
        Assert.Equal(1, snapshot.Orders);
    }

    [Fact]
    public void AgedOutEventsAreEvictedFromMemory()
    {
        var (aggregator, time) = Build(windowMinutes: 1);

        for (var i = 0; i < 50; i++)
            aggregator.Ingest(Sale(10m, Origin));

        Assert.Equal(50, aggregator.Count);

        time.Advance(TimeSpan.FromMinutes(2));
        aggregator.Ingest(Sale(10m, time.GetUtcNow()));

        // Ingest triggers eviction, so the buffer must not keep growing.
        Assert.Equal(1, aggregator.Count);
    }

    [Fact]
    public void EventOnTheWindowBoundaryIsExcluded()
    {
        var (aggregator, time) = Build(windowMinutes: 5);

        aggregator.Ingest(Sale(100m, Origin));
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0m, aggregator.Snapshot().Revenue);
    }

    [Fact]
    public void OutOfOrderEventStillCountsWhenInsideTheWindow()
    {
        var (aggregator, time) = Build(windowMinutes: 5);
        time.Advance(TimeSpan.FromMinutes(2));

        // Arrives late but happened one minute ago — still inside the window.
        aggregator.Ingest(Sale(75m, time.GetUtcNow() - TimeSpan.FromMinutes(1)));

        Assert.Equal(75m, aggregator.Snapshot().Revenue);
    }

    [Fact]
    public void TimelineCoversTheWholeWindow()
    {
        var (aggregator, _) = Build(windowMinutes: 5, bucketSeconds: 30);

        aggregator.Ingest(Sale(100m, Origin));

        // 5 minutes / 30s = 10 buckets.
        Assert.Equal(10, aggregator.Snapshot().Timeline.Count);
    }

    [Fact]
    public void QuietBucketsAppearAsZeroNotAsGaps()
    {
        var (aggregator, time) = Build(windowMinutes: 2, bucketSeconds: 30);

        aggregator.Ingest(Sale(100m, Origin));
        time.Advance(TimeSpan.FromMinutes(1));

        var timeline = aggregator.Snapshot().Timeline;

        Assert.Equal(4, timeline.Count);
        Assert.Contains(timeline, b => b.Revenue == 0m);
        Assert.Contains(timeline, b => b.Revenue == 100m);
    }

    [Fact]
    public void TimelineIsOrderedOldestFirst()
    {
        var (aggregator, _) = Build();

        aggregator.Ingest(Sale(10m, Origin));

        var timeline = aggregator.Snapshot().Timeline;
        var sorted = timeline.OrderBy(b => b.StartsAt).ToArray();

        Assert.Equal(sorted.Select(b => b.StartsAt), timeline.Select(b => b.StartsAt));
    }

    [Fact]
    public void TimelineRevenueMatchesTheHeadline()
    {
        var (aggregator, _) = Build();

        aggregator.Ingest(Sale(30m, Origin));
        aggregator.Ingest(Sale(70m, Origin));

        var snapshot = aggregator.Snapshot();

        Assert.Equal(snapshot.Revenue, snapshot.Timeline.Sum(b => b.Revenue));
        Assert.Equal(snapshot.Orders, snapshot.Timeline.Sum(b => b.Orders));
    }

    [Fact]
    public void InvalidEventIsRejected()
    {
        var (aggregator, _) = Build();

        Assert.Throws<ArgumentException>(() =>
            aggregator.Ingest(Sale(-5m, Origin)));
        Assert.Equal(0, aggregator.Count);
    }

    [Fact]
    public void NullEventIsRejected()
    {
        var (aggregator, _) = Build();
        Assert.Throws<ArgumentNullException>(() => aggregator.Ingest(null!));
    }

    [Fact]
    public void BucketLargerThanWindowIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlidingWindowAggregator(
                TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ZeroBucketSizeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlidingWindowAggregator(TimeSpan.FromMinutes(5), TimeSpan.Zero));
    }

    [Fact]
    public void ConcurrentIngestLosesNothing()
    {
        var (aggregator, _) = Build();
        const int writers = 8;
        const int perWriter = 500;

        Parallel.For(0, writers, _ =>
        {
            for (var i = 0; i < perWriter; i++)
                aggregator.Ingest(Sale(1m, Origin));
        });

        var snapshot = aggregator.Snapshot();
        Assert.Equal(writers * perWriter, snapshot.Orders);
        Assert.Equal(writers * perWriter, snapshot.Revenue);
    }

    [Fact]
    public async Task SnapshotsDuringConcurrentWritesStayInternallyConsistent()
    {
        var (aggregator, _) = Build();
        var stop = false;
        var failures = 0;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var snapshot = aggregator.Snapshot();
                // A torn read would show totals that disagree with the parts.
                if (snapshot.Orders != snapshot.ByRegion.Sum(r => r.Orders))
                    Interlocked.Increment(ref failures);
            }
        });

        Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < 500; i++)
                aggregator.Ingest(Sale(2m, Origin));
        });

        Volatile.Write(ref stop, true);
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, failures);
    }

    [Fact]
    public void SnapshotIsAPointInTimeCopy()
    {
        var (aggregator, _) = Build();
        aggregator.Ingest(Sale(10m, Origin));

        var before = aggregator.Snapshot();
        aggregator.Ingest(Sale(90m, Origin));

        // The earlier snapshot must not observe the later event.
        Assert.Equal(10m, before.Revenue);
        Assert.Equal(100m, aggregator.Snapshot().Revenue);
    }
}

/// <summary>Controllable clock so window behaviour is tested without sleeping.</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
