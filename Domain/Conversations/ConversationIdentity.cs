namespace Domain.Conversations;

public record ConversationIdentity(string TopicId, long ChatId, long ThreadId, string ConversationId);