// McpServerHomeAssistant/Settings/McpSettings.cs
namespace McpServerHomeAssistant.Settings;

public record McpSettings
{
    public required HomeAssistantConfiguration HomeAssistant { get; init; }
}

public record HomeAssistantConfiguration
{
    public required string BaseUrl { get; init; }
    public required string Token { get; init; }
}