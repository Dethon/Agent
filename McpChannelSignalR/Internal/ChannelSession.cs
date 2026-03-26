namespace McpChannelSignalR.Internal;

public record ChannelSession(string AgentId, long ChatId, long ThreadId, string? SpaceSlug = null);