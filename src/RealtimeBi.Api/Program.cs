using Microsoft.Extensions.Options;
using RealtimeBi.Api.Domain;
using RealtimeBi.Api.Hubs;
using RealtimeBi.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FeedOptions>(
    builder.Configuration.GetSection(FeedOptions.SectionName));

// Fail at startup rather than serving a misconfigured dashboard.
builder.Services.AddOptions<FeedOptions>()
    .Validate(options => options.Validate().Count == 0,
        "Feed configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddSignalR();
builder.Services.AddSingleton<IConnectionTracker, ConnectionTracker>();
builder.Services.AddSingleton<SalesEventGenerator>();

builder.Services.AddSingleton<IAggregator>(provider =>
{
    var options = provider.GetRequiredService<IOptions<FeedOptions>>().Value;
    return new SlidingWindowAggregator(
        window: TimeSpan.FromMinutes(options.WindowMinutes),
        bucketSize: TimeSpan.FromSeconds(options.BucketSeconds));
});

builder.Services.AddHostedService<FeedWorker>();

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(
            builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
            ?? ["http://localhost:4200", "http://localhost:5173"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials())); // SignalR needs credentials for its handshake.

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<DashboardHub>("/hub/dashboard");

app.MapGet("/health", (
    IAggregator aggregator,
    IConnectionTracker connections,
    IOptions<FeedOptions> options) => Results.Ok(new
    {
        status = "ok",
        eventsInWindow = aggregator.Count,
        connectedClients = connections.Count,
        windowMinutes = options.Value.WindowMinutes,
        eventsPerSecond = options.Value.EventsPerSecond,
    }));

app.MapGet("/api/snapshot", (IAggregator aggregator) =>
    Results.Ok(aggregator.Snapshot()));

// Lets a real order system push events in, instead of relying on the
// synthetic feed.
app.MapPost("/api/events", (SalesEvent salesEvent, IAggregator aggregator) =>
{
    var errors = salesEvent.Validate();
    if (errors.Count > 0)
        return Results.ValidationProblem(
            new Dictionary<string, string[]> { ["salesEvent"] = [.. errors] });

    aggregator.Ingest(salesEvent);
    return Results.Accepted("/api/snapshot");
});

app.Run();

// Exposed so the integration tests can spin the app up with WebApplicationFactory.
public partial class Program;
