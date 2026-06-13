using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ConversationContext(string AgentId, string ConversationId, string UserId, ReplyTarget Origin);