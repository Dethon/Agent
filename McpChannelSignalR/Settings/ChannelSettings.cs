namespace McpChannelSignalR.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
    public string AgentUrl { get; init; } = "http://agent:8080";
    public AgentConfig[] Agents { get; init; } = [];
    public WebPushConfig? WebPush { get; init; }
}

public record WebPushConfig
{
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Subject { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);
}

public record AgentConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
}
