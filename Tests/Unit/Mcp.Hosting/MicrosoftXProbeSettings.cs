namespace MicrosoftX;

// The Microsoft half of the same near miss.
public record ProbeNearMissSettings
{
    public required ProbeNearMissSearch Search { get; init; }
}

public record ProbeNearMissSearch
{
    public required string ApiKey { get; init; }

    public string ApiUrl { get; init; } = "https://example";
}