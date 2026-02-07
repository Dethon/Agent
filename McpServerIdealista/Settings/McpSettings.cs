namespace McpServerIdealista.Settings;

public record McpSettings
{
    public required IdealistaConfiguration Idealista { get; init; }
}

public record IdealistaConfiguration
{
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }
    public string ApiUrl { get; init; } = "https://api.idealista.com/";
}