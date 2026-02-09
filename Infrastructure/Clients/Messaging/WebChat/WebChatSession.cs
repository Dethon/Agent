namespace Infrastructure.Clients.Messaging.WebChat;

public record WebChatSession(string AgentId, long ChatId, long ThreadId, string? SpaceSlug = null);