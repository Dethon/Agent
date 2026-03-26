using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpChannelSignalR;

public class RedisStateServiceTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private readonly RedisStateService _sut = new(redis.Connection);

    [Fact]
    public async Task GetAllTopicsAsync_FiltersBySpaceSlug()
    {
        var topic1 = new TopicMetadata("t-s1", 300, 0, "agent-slug", "Space1", DateTimeOffset.UtcNow, null, SpaceSlug: "space-a");
        var topic2 = new TopicMetadata("t-s2", 301, 0, "agent-slug", "Space2", DateTimeOffset.UtcNow, null, SpaceSlug: "space-b");

        await _sut.SaveTopicAsync(topic1);
        await _sut.SaveTopicAsync(topic2);

        var filtered = await _sut.GetAllTopicsAsync("agent-slug", "space-a");
        filtered.ShouldContain(t => t.TopicId == "t-s1");
        filtered.ShouldNotContain(t => t.TopicId == "t-s2");
    }

}
