using McpChannelSignalR.Services;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class SessionServiceTests
{
    private readonly SessionService _sut = new();

    [Fact]
    public void StartSession_StoresAndRetrievesSession()
    {
        _sut.StartSession("topic1", "agent1", 100, 200);

        _sut.TryGetSession("topic1", out var session).ShouldBeTrue();
        session!.AgentId.ShouldBe("agent1");
        session.ChatId.ShouldBe(100);
        session.ThreadId.ShouldBe(200);
    }

    [Fact]
    public void TryGetSession_UnknownTopic_ReturnsFalse()
    {
        _sut.TryGetSession("nonexistent", out _).ShouldBeFalse();
    }

    [Fact]
    public void EndSession_RemovesSessionAndMappings()
    {
        _sut.StartSession("topic1", "agent1", 100, 200);
        _sut.EndSession("topic1");

        _sut.TryGetSession("topic1", out _).ShouldBeFalse();
        _sut.GetTopicIdByChatId(100).ShouldBeNull();
        _sut.GetTopicIdByConversationId("100:200").ShouldBeNull();
    }

    [Fact]
    public void GetTopicIdByChatId_ReturnsCorrectMapping()
    {
        _sut.StartSession("topic1", "agent1", 100, 200);
        _sut.GetTopicIdByChatId(100).ShouldBe("topic1");
    }

    [Fact]
    public void GetTopicIdByConversationId_ReturnsCorrectMapping()
    {
        _sut.StartSession("topic1", "agent1", 100, 200);
        _sut.GetTopicIdByConversationId("100:200").ShouldBe("topic1");
    }

    [Fact]
    public void GetSessionByConversationId_ReturnsSession()
    {
        _sut.StartSession("topic1", "agent1", 100, 200);

        var session = _sut.GetSessionByConversationId("100:200");
        session.ShouldNotBeNull();
        session.AgentId.ShouldBe("agent1");
    }

    [Fact]
    public void GetSessionByConversationId_UnknownId_ReturnsNull()
    {
        _sut.GetSessionByConversationId("999:999").ShouldBeNull();
    }

    [Fact]
    public async Task CreateConversationAsync_ReturnsConversationIdAndCreatesSession()
    {
        var conversationId = await _sut.CreateConversationAsync(
            new CreateConversationParams { AgentId = "agent1", TopicName = "Test topic", Sender = "user1" });

        conversationId.ShouldNotBeNullOrEmpty();
        conversationId.ShouldContain(":");

        var session = _sut.GetSessionByConversationId(conversationId);
        session.ShouldNotBeNull();
        session.AgentId.ShouldBe("agent1");
    }

    [Fact]
    public void EndSession_NonexistentTopic_DoesNotThrow()
    {
        Should.NotThrow(() => _sut.EndSession("nonexistent"));
    }
}
