using Domain.DTOs.WebChat;
using WebChat.Client.Services;

namespace WebChat.Client.Models;

public class StoredTopic
{
    public string TopicId { get; set; } = "";
    public long ChatId { get; set; }
    public long ThreadId { get; set; }
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "New Chat";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }

    public static StoredTopic FromMetadata(TopicMetadata metadata)
    {
        // Always derive ChatId and ThreadId from TopicId for consistency
        return new StoredTopic
        {
            TopicId = metadata.TopicId,
            ChatId = ChatHubService.GetChatIdForTopic(metadata.TopicId),
            ThreadId = ChatHubService.GetThreadIdForTopic(metadata.TopicId),
            AgentId = metadata.AgentId,
            Name = metadata.Name,
            CreatedAt = metadata.CreatedAt.UtcDateTime,
            LastMessageAt = metadata.LastMessageAt?.UtcDateTime
        };
    }

    public TopicMetadata ToMetadata()
    {
        return new TopicMetadata(
            TopicId,
            ChatId,
            ThreadId,
            AgentId,
            Name,
            new DateTimeOffset(CreatedAt, TimeSpan.Zero),
            LastMessageAt.HasValue ? new DateTimeOffset(LastMessageAt.Value, TimeSpan.Zero) : null);
    }
}