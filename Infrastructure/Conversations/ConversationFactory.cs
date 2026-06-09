using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace Infrastructure.Conversations;

public sealed class ConversationFactory(IThreadStateStore store, TimeProvider time) : IConversationFactory
{
    public async Task<ConversationCreation> CreateAsync(CreateConversationParams p, CancellationToken ct = default)
    {
        var identity = ConversationIdGenerator.Create();
        var topic = new TopicMetadata(
            identity.TopicId,
            identity.ChatId,
            identity.ThreadId,
            p.AgentId,
            p.TopicName,
            time.GetUtcNow(),
            LastMessageAt: null,
            SpaceSlug: "default");

        await store.SaveTopicAsync(topic);
        return new ConversationCreation(identity, topic);
    }
}