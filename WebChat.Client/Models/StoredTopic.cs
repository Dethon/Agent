using Domain.DTOs.WebChat;
using WebChat.Client.Services.Utilities;

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
    public int LastReadMessageCount { get; set; }

    public static StoredTopic FromMetadata(TopicMetadata metadata)
    {
        return new StoredTopic
        {
            TopicId = metadata.TopicId,
            ChatId = TopicIdGenerator.GetChatIdForTopic(metadata.TopicId),
            ThreadId = TopicIdGenerator.GetThreadIdForTopic(metadata.TopicId),
            AgentId = metadata.AgentId,
            Name = metadata.Name,
            CreatedAt = metadata.CreatedAt.UtcDateTime,
            LastMessageAt = metadata.LastMessageAt?.UtcDateTime,
            LastReadMessageCount = metadata.LastReadMessageCount
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
            LastMessageAt.HasValue ? new DateTimeOffset(LastMessageAt.Value, TimeSpan.Zero) : null,
            LastReadMessageCount);
    }

    public StoredTopic ApplyMetadata(TopicMetadata metadata)
    {
        return new StoredTopic
        {
            TopicId = TopicId,
            ChatId = ChatId,
            ThreadId = ThreadId,
            AgentId = AgentId,
            Name = metadata.Name,
            CreatedAt = CreatedAt,
            LastMessageAt = metadata.LastMessageAt?.DateTime,
            LastReadMessageCount = metadata.LastReadMessageCount
        };
    }
}