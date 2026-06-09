using Domain.DTOs.WebChat;

namespace Domain.Conversations;

public record ConversationCreation(ConversationIdentity Identity, TopicMetadata Topic);