using McpChannelSignalR.Internal;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class BroadcastChannelTests
{
    [Fact]
    public async Task WriteAsync_DeliversToAllSubscribers()
    {
        var sut = new BroadcastChannel<string>();
        var reader1 = sut.Subscribe();
        var reader2 = sut.Subscribe();

        await sut.WriteAsync("hello", CancellationToken.None);

        (await reader1.ReadAsync()).ShouldBe("hello");
        (await reader2.ReadAsync()).ShouldBe("hello");
    }

    [Fact]
    public async Task WriteAsync_DoesNotDeliverToLateSubscriber()
    {
        var sut = new BroadcastChannel<string>();
        await sut.WriteAsync("before", CancellationToken.None);

        var lateReader = sut.Subscribe();
        await sut.WriteAsync("after", CancellationToken.None);

        (await lateReader.ReadAsync()).ShouldBe("after");
    }

    [Fact]
    public async Task Complete_EndsAllSubscribers()
    {
        var sut = new BroadcastChannel<string>();
        var reader = sut.Subscribe();

        sut.Complete();

        reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task WriteAsync_NoSubscribers_DoesNotThrow()
    {
        var sut = new BroadcastChannel<string>();
        await Should.NotThrowAsync(() => sut.WriteAsync("orphan", CancellationToken.None));
    }
}