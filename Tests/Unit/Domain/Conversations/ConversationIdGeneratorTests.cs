using Domain.Conversations;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class ConversationIdGeneratorTests
{
    [Fact]
    public void CreateFor_IsDeterministicForSameTopicId()
    {
        var a = ConversationIdGenerator.CreateFor("topic-abc");
        var b = ConversationIdGenerator.CreateFor("topic-abc");

        a.ChatId.ShouldBe(b.ChatId);
        a.ThreadId.ShouldBe(b.ThreadId);
        a.ConversationId.ShouldBe(b.ConversationId);
    }

    [Fact]
    public void CreateFor_FormatsConversationIdAsChatColonThread()
    {
        var id = ConversationIdGenerator.CreateFor("topic-abc");

        id.ConversationId.ShouldBe($"{id.ChatId}:{id.ThreadId}");
        id.TopicId.ShouldBe("topic-abc");
    }

    [Fact]
    public void CreateFor_ProducesNonNegativeIds()
    {
        var id = ConversationIdGenerator.CreateFor("topic-abc");

        id.ChatId.ShouldBeGreaterThanOrEqualTo(0);
        id.ThreadId.ShouldBeGreaterThanOrEqualTo(0);
        id.ThreadId.ShouldBeLessThanOrEqualTo(0x7FFFFFFF);
    }

    [Fact]
    public void Create_GeneratesDistinctConversations()
    {
        var a = ConversationIdGenerator.Create();
        var b = ConversationIdGenerator.Create();

        a.ConversationId.ShouldNotBe(b.ConversationId);
    }
}