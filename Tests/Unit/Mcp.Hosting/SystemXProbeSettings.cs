namespace SystemX;

// Deliberately in a namespace that merely starts with the letters "System": the framework-type
// exclusion is a namespace check, and a prefix match with no trailing dot reads this as the BCL.
public record ProbeNearMissSettings
{
    public required ProbeNearMissSearch Search { get; init; }
}

public record ProbeNearMissSearch
{
    public required string ApiKey { get; init; }

    public string ApiUrl { get; init; } = "https://example";
}