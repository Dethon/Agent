using Domain.DTOs;
using Domain.Memory;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryExtractionQueueTests
{
    [Fact]
    public async Task EnqueueAsync_AndReadAllAsync_ReturnsEnqueuedItem()
    {
        var queue = new MemoryExtractionQueue();
        var request = new MemoryExtractionRequest("user1", "Hello", "conv_1");

        await queue.EnqueueAsync(request, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var items = new List<MemoryExtractionRequest>();
        await foreach (var item in queue.ReadAllAsync(cts.Token))
        {
            items.Add(item);
            break;
        }

        items.Count.ShouldBe(1);
        items[0].UserId.ShouldBe("user1");
        items[0].MessageContent.ShouldBe("Hello");
    }

    [Fact]
    public async Task EnqueueAsync_MultipleItems_ReadsInOrder()
    {
        var queue = new MemoryExtractionQueue();

        await queue.EnqueueAsync(new MemoryExtractionRequest("user1", "First", null), CancellationToken.None);
        await queue.EnqueueAsync(new MemoryExtractionRequest("user2", "Second", null), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var items = new List<MemoryExtractionRequest>();
        await foreach (var item in queue.ReadAllAsync(cts.Token))
        {
            items.Add(item);
            if (items.Count == 2)
            {
                break;
            }

        }

        items[0].MessageContent.ShouldBe("First");
        items[1].MessageContent.ShouldBe("Second");
    }
}
