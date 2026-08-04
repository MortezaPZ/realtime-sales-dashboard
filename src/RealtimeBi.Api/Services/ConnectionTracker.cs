using System.Collections.Concurrent;

namespace RealtimeBi.Api.Services;

public interface IConnectionTracker
{
    void Add(string connectionId);
    void Remove(string connectionId);
    int Count { get; }
}

/// <summary>
/// Counts live dashboard connections so the broadcaster can skip work when
/// nobody is watching, and so /health can report it.
/// </summary>
public sealed class ConnectionTracker : IConnectionTracker
{
    private readonly ConcurrentDictionary<string, byte> _connections = new();

    public void Add(string connectionId) => _connections.TryAdd(connectionId, 0);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public int Count => _connections.Count;
}
