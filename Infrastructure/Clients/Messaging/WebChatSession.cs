namespace Infrastructure.Clients.Messaging;

public record WebChatSession(string AgentId, long ChatId, long ThreadId);