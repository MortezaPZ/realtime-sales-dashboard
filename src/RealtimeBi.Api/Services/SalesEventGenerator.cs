using RealtimeBi.Api.Domain;

namespace RealtimeBi.Api.Services;

/// <summary>
/// Produces synthetic sales so the dashboard has a live feed without needing a
/// real order system wired up. Seeded, so a run is reproducible in tests.
/// </summary>
public sealed class SalesEventGenerator
{
    private static readonly string[] Regions =
        ["North", "South", "East", "West", "Central"];

    private static readonly string[] Channels =
        ["Web", "Mobile", "Partner", "In-store"];

    // Weights give the feed a believable shape rather than a uniform smear.
    private static readonly double[] RegionWeights = [0.30, 0.25, 0.18, 0.15, 0.12];
    private static readonly double[] ChannelWeights = [0.45, 0.32, 0.14, 0.09];

    private readonly Random _random;
    private readonly TimeProvider _time;
    private int _sequence;

    public SalesEventGenerator(int seed = 7, TimeProvider? timeProvider = null)
    {
        _random = new Random(seed);
        _time = timeProvider ?? TimeProvider.System;
    }

    public SalesEvent Next()
    {
        var id = Interlocked.Increment(ref _sequence);

        // Log-normal-ish amounts: many small orders, a few large ones.
        var magnitude = Math.Exp(_random.NextDouble() * 2.6);
        var amount = decimal.Round((decimal)(12 + magnitude * 18), 2);

        return new SalesEvent
        {
            OrderId = $"ORD-{id:D8}",
            Region = Pick(Regions, RegionWeights),
            Channel = Pick(Channels, ChannelWeights),
            Amount = amount,
            OccurredAt = _time.GetUtcNow(),
        };
    }

    public IEnumerable<SalesEvent> Take(int count)
    {
        for (var i = 0; i < count; i++)
            yield return Next();
    }

    private string Pick(string[] options, double[] weights)
    {
        var roll = _random.NextDouble();
        var cumulative = 0.0;

        for (var i = 0; i < options.Length; i++)
        {
            cumulative += weights[i];
            if (roll <= cumulative)
                return options[i];
        }

        // Floating-point drift can leave the roll a hair above the final total.
        return options[^1];
    }
}
