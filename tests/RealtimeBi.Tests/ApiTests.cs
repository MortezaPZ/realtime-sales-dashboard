using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RealtimeBi.Api.Domain;
using RealtimeBi.Api.Services;

namespace RealtimeBi.Tests;

/// <summary>
/// Boots the API without the synthetic feed so assertions are about the events
/// a test pushes in, not about whatever the generator happened to produce.
/// </summary>
public sealed class QuietApiFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Feed:GenerateSyntheticEvents"] = "false",
                ["Feed:EventsPerSecond"] = "0",
                ["Feed:BroadcastIntervalMs"] = "200",
                ["Feed:WindowMinutes"] = "5",
                ["Feed:BucketSeconds"] = "30",
            }));

        return base.CreateHost(builder);
    }
}

public class ApiTests : IClassFixture<QuietApiFactory>
{
    private readonly QuietApiFactory _factory;

    public ApiTests(QuietApiFactory factory) => _factory = factory;

    private static SalesEvent Sale(decimal amount, string region = "North") => new()
    {
        OrderId = $"ORD-{Guid.NewGuid():N}",
        Region = region,
        Channel = "Web",
        Amount = amount,
        OccurredAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task HealthReportsState()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("windowMinutes").GetInt32());
    }

    [Fact]
    public async Task SnapshotIsServedOverHttp()
    {
        var client = _factory.CreateClient();

        var snapshot = await client.GetFromJsonAsync<DashboardSnapshot>("/api/snapshot");

        Assert.NotNull(snapshot);
        Assert.Equal(5, snapshot!.WindowMinutes);
    }

    [Fact]
    public async Task PostedEventReachesTheSnapshot()
    {
        var client = _factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/events", Sale(123.45m, "West"));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        var snapshot = await client.GetFromJsonAsync<DashboardSnapshot>("/api/snapshot");
        Assert.Contains(snapshot!.ByRegion, r => r.Key == "West" && r.Revenue >= 123.45m);
    }

    [Fact]
    public async Task InvalidEventIsRejectedWithDetails()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/events", new
        {
            orderId = "",
            region = "North",
            channel = "Web",
            amount = -5,
            occurredAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OrderId", body);
        Assert.Contains("Amount", body);
    }

    [Fact]
    public async Task ConnectingClientReceivesASnapshotImmediately()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/events", Sale(50m, "East"));

        await using var connection = BuildConnection();
        var received = new TaskCompletionSource<DashboardSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<DashboardSnapshot>("snapshot", snapshot =>
            received.TrySetResult(snapshot));

        await connection.StartAsync();

        // The hub pushes on connect — no need to wait for a broadcast tick.
        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);

        var result = await received.Task;
        Assert.Contains(result.ByRegion, r => r.Key == "East");
    }

    [Fact]
    public async Task BroadcastPushesUpdatesToConnectedClients()
    {
        var client = _factory.CreateClient();

        await using var connection = BuildConnection();
        var snapshots = new List<DashboardSnapshot>();
        var secondArrived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<DashboardSnapshot>("snapshot", snapshot =>
        {
            lock (snapshots)
            {
                snapshots.Add(snapshot);
                if (snapshots.Count >= 2)
                    secondArrived.TrySetResult();
            }
        });

        await connection.StartAsync();
        await client.PostAsJsonAsync("/api/events", Sale(999m, "Central"));

        var completed = await Task.WhenAny(
            secondArrived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(secondArrived.Task, completed);

        lock (snapshots)
        {
            Assert.True(snapshots.Count >= 2);
            Assert.Contains(snapshots[^1].ByRegion, r => r.Key == "Central");
        }
    }

    [Fact]
    public async Task ClientCanPullASnapshotOnDemand()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/events", Sale(77m, "South"));

        await using var connection = BuildConnection();
        await connection.StartAsync();

        var snapshot = await connection.InvokeAsync<DashboardSnapshot>("RequestSnapshot");

        Assert.Contains(snapshot.ByRegion, r => r.Key == "South");
    }

    [Fact]
    public async Task ConnectionCountTracksConnectAndDisconnect()
    {
        var tracker = _factory.Services.GetRequiredService<IConnectionTracker>();

        // Other tests in this class share the factory, and their disconnects
        // land asynchronously. Wait for the tracker to settle before taking a
        // baseline, or this test races whichever test ran before it.
        var before = await WaitForStableCount(tracker);

        await using (var connection = BuildConnection())
        {
            await connection.StartAsync();
            Assert.True(
                await WaitUntil(() => tracker.Count == before + 1),
                $"Expected {before + 1} connections, saw {tracker.Count}.");
        }

        Assert.True(
            await WaitUntil(() => tracker.Count == before),
            $"Expected the count to return to {before}, saw {tracker.Count}.");
    }

    private static async Task<bool> WaitUntil(
        Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    private static async Task<int> WaitForStableCount(
        IConnectionTracker tracker, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var last = tracker.Count;
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            var current = tracker.Count;

            if (current != last)
            {
                last = current;
                stableSince = DateTime.UtcNow;
                continue;
            }

            if (DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(300))
                return current;
        }

        return tracker.Count;
    }

    private HubConnection BuildConnection() =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "hub/dashboard"),
                options => options.HttpMessageHandlerFactory = _ =>
                    _factory.Server.CreateHandler())
            .Build();
}
