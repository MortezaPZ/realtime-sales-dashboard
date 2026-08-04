namespace RealtimeBi.Api.Domain;

/// <summary>Totals for one dimension value (a region, a channel).</summary>
public sealed record Breakdown
{
    public required string Key { get; init; }
    public required decimal Revenue { get; init; }
    public required int Orders { get; init; }

    public decimal AverageOrderValue =>
        Orders == 0 ? 0m : decimal.Round(Revenue / Orders, 2);
}

/// <summary>One bucket on the revenue timeline.</summary>
public sealed record TimeBucket
{
    public required DateTimeOffset StartsAt { get; init; }
    public required decimal Revenue { get; init; }
    public required int Orders { get; init; }
}

/// <summary>
/// What the dashboard renders: totals over the live window, split by region and
/// channel, plus a timeline. Immutable, so a snapshot pushed to a client can
/// never be mutated by the next event to arrive.
/// </summary>
public sealed record DashboardSnapshot
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required int WindowMinutes { get; init; }
    public required decimal Revenue { get; init; }
    public required int Orders { get; init; }
    public required decimal AverageOrderValue { get; init; }
    public required IReadOnlyList<Breakdown> ByRegion { get; init; }
    public required IReadOnlyList<Breakdown> ByChannel { get; init; }
    public required IReadOnlyList<TimeBucket> Timeline { get; init; }

    public static DashboardSnapshot Empty(int windowMinutes) => new()
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        WindowMinutes = windowMinutes,
        Revenue = 0m,
        Orders = 0,
        AverageOrderValue = 0m,
        ByRegion = [],
        ByChannel = [],
        Timeline = [],
    };
}
