using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RealtimeBi.Api.Hubs;

namespace RealtimeBi.Api.Services;

/// <summary>
/// Two independent timers: one feeds synthetic events into the aggregator, one
/// pushes snapshots to connected dashboards.
///
/// They are deliberately decoupled. Broadcasting per event would flood clients
/// at high throughput; broadcasting on a fixed tick means the client cost stays
/// flat no matter how fast events arrive.
/// </summary>
public sealed class FeedWorker : BackgroundService
{
    private readonly IAggregator _aggregator;
    private readonly IHubContext<DashboardHub> _hub;
    private readonly IConnectionTracker _connections;
    private readonly SalesEventGenerator _generator;
    private readonly FeedOptions _options;
    private readonly ILogger<FeedWorker> _logger;

    public FeedWorker(
        IAggregator aggregator,
        IHubContext<DashboardHub> hub,
        IConnectionTracker connections,
        SalesEventGenerator generator,
        IOptions<FeedOptions> options,
        ILogger<FeedWorker> logger)
    {
        _aggregator = aggregator;
        _hub = hub;
        _connections = connections;
        _generator = generator;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Feed starting: {Rate}/s, broadcast every {Interval}ms, {Window}m window",
            _options.EventsPerSecond,
            _options.BroadcastIntervalMs,
            _options.WindowMinutes);

        var tasks = new List<Task> { BroadcastLoop(stoppingToken) };
        if (_options.GenerateSyntheticEvents && _options.EventsPerSecond > 0)
            tasks.Add(ProduceLoop(stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task ProduceLoop(CancellationToken token)
    {
        // Emit in small batches rather than one-per-timer-tick: a 12/s rate would
        // otherwise need an 83ms timer, which the OS scheduler cannot hold
        // accurately.
        var interval = TimeSpan.FromMilliseconds(200);
        var perBatch = Math.Max(1, (int)Math.Round(_options.EventsPerSecond * interval.TotalSeconds));

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (var salesEvent in _generator.Take(perBatch))
                    _aggregator.Ingest(salesEvent);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task BroadcastLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(_options.BroadcastIntervalMs));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_connections.Count == 0)
                    continue; // Nobody watching — don't pay for the projection.

                try
                {
                    var snapshot = _aggregator.Snapshot();
                    await _hub.Clients.All.SendAsync(
                        DashboardHub.SnapshotMethod, snapshot, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad broadcast must not kill the loop; the next tick retries.
                    _logger.LogError(ex, "Broadcast failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
