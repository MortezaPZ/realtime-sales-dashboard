namespace RealtimeBi.Api.Domain;

/// <summary>A single completed sale flowing through the pipeline.</summary>
public sealed record SalesEvent
{
    public required string OrderId { get; init; }
    public required string Region { get; init; }
    public required string Channel { get; init; }
    public required decimal Amount { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Validates the fields the aggregator relies on. Bad events are rejected at
    /// the edge so a single malformed payload cannot corrupt a running window.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(OrderId))
            errors.Add("OrderId is required.");
        if (string.IsNullOrWhiteSpace(Region))
            errors.Add("Region is required.");
        if (string.IsNullOrWhiteSpace(Channel))
            errors.Add("Channel is required.");
        if (Amount <= 0)
            errors.Add("Amount must be greater than zero.");
        if (OccurredAt == default)
            errors.Add("OccurredAt is required.");

        return errors;
    }

    public bool IsValid => Validate().Count == 0;
}
