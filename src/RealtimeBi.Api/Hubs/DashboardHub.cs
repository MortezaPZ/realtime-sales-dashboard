using Microsoft.AspNetCore.SignalR;
using RealtimeBi.Api.Domain;
using RealtimeBi.Api.Services;

namespace RealtimeBi.Api.Hubs;

/// <summary>
/// The dashboard's realtime channel. A client that has just connected would
/// otherwise stare at an empty screen until the next broadcast tick, so it is
/// sent the current snapshot immediately on connect.
/// </summary>
public sealed class DashboardHub : Hub
{
    public const string SnapshotMethod = "snapshot";

    private readonly IAggregator _aggregator;
    private readonly IConnectionTracker _connections;

    public DashboardHub(IAggregator aggregator, IConnectionTracker connections)
    {
        _aggregator = aggregator;
        _connections = connections;
    }

    public override async Task OnConnectedAsync()
    {
        _connections.Add(Context.ConnectionId);
        await Clients.Caller.SendAsync(SnapshotMethod, _aggregator.Snapshot());
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Lets a client pull a fresh snapshot without waiting for a tick.</summary>
    public DashboardSnapshot RequestSnapshot() => _aggregator.Snapshot();
}
