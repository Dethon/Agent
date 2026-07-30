namespace McpServerHomeAssistant.Settings;

public record McpSettings
{
    public required HomeAssistantConfiguration HomeAssistant { get; init; }

    // Optional: Music Assistant's own API, used only for the podcast-episode listing Home Assistant
    // cannot provide. Absent or tokenless, the server simply does not expose that action.
    public MusicAssistantConfiguration? MusicAssistant { get; init; }
}

public record HomeAssistantConfiguration
{
    public required string BaseUrl { get; init; }
    public required string Token { get; init; }
}

public record MusicAssistantConfiguration
{
    public required string BaseUrl { get; init; }
    public string Token { get; init; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);
}