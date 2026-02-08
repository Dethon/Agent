namespace McpServerWebSearch.Settings;

public record McpSettings
{
    public required BraveSearchConfiguration BraveSearch { get; init; }
    public CapSolverConfiguration? CapSolver { get; init; }
}

public record BraveSearchConfiguration
{
    public required string ApiKey { get; init; }
    public string ApiUrl { get; init; } = "https://api.search.brave.com/res/v1/";
}

public record CapSolverConfiguration
{
    public required string ApiKey { get; init; }
}