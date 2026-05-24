using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ReplyTarget(string ChannelId, string? ConversationId);