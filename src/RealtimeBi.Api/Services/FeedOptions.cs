namespace RealtimeBi.Api.Services;

/// <summary>Tunables for the synthetic feed and the broadcast loop.</summary>
public sealed class FeedOptions
{
    public const string SectionName = "Feed";

    /// <summary>Synthetic orders generated per second.</summary>
    public int EventsPerSecond { get; set; } = 12;

    /// <summary>How often a snapshot is pushed to connected dashboards.</summary>
    public int BroadcastIntervalMs { get; set; } = 1000;

    /// <summary>How much history the live window keeps.</summary>
    public int WindowMinutes { get; set; } = 5;

    /// <summary>Timeline granularity.</summary>
    public int BucketSeconds { get; set; } = 30;

    /// <summary>Set false to run the API without the synthetic producer.</summary>
    public bool GenerateSyntheticEvents { get; set; } = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (EventsPerSecond is < 0 or > 10_000)
            errors.Add("EventsPerSecond must be between 0 and 10000.");
        if (BroadcastIntervalMs is < 100 or > 60_000)
            errors.Add("BroadcastIntervalMs must be between 100 and 60000.");
        if (WindowMinutes is < 1 or > 1440)
            errors.Add("WindowMinutes must be between 1 and 1440.");
        if (BucketSeconds < 1)
            errors.Add("BucketSeconds must be at least 1.");
        if (BucketSeconds > WindowMinutes * 60)
            errors.Add("BucketSeconds cannot exceed the window length.");

        return errors;
    }
}
