using Domain.Monitor;
using Shouldly;

namespace Tests.Unit.Domain;

public class TaskQueueTests
{
    [Fact]
    public async Task QueueTask_AddsToQueue()
    {
        // Arrange
        var queue = new TaskQueue();

        // Act
        await queue.QueueTask(_ => Task.CompletedTask, CancellationToken.None);

        // Assert
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DequeueTask_ReturnsQueuedTask()
    {
        // Arrange
        var queue = new TaskQueue();
        var executed = false;
        Func<CancellationToken, Task> expectedTask = _ =>
        {
            executed = true;
            return Task.CompletedTask;
        };
        await queue.QueueTask(expectedTask, CancellationToken.None);

        // Act
        var dequeuedTask = await queue.DequeueTask(CancellationToken.None);
        await dequeuedTask(CancellationToken.None);

        // Assert
        executed.ShouldBeTrue();
        queue.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Queue_WhenFull_Waits()
    {
        // Arrange
        var queue = new TaskQueue(2);
        await queue.QueueTask(_ => Task.CompletedTask, CancellationToken.None);
        await queue.QueueTask(_ => Task.CompletedTask, CancellationToken.None);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var queueTask = queue.QueueTask(_ => Task.CompletedTask, cts.Token);

        // Assert - the queue should be blocked
        var completedBeforeTimeout = queueTask.IsCompleted;
        completedBeforeTimeout.ShouldBeFalse();
    }

    [Fact]
    public async Task Queue_WhenFullAndItemDequeued_Unblocks()
    {
        // Arrange
        var queue = new TaskQueue(2);
        await queue.QueueTask(_ => Task.CompletedTask, CancellationToken.None);
        await queue.QueueTask(_ => Task.CompletedTask, CancellationToken.None);

        // Act - start the blocking queue operation
        var queueCompleted = false;
        var queueTask = Task.Run(async () =>
        {
            await queue.QueueTask(_ => Task.CompletedTask, CancellationToken.None);
            queueCompleted = true;
        });

        // Give time for queueTask to start blocking
        await Task.Delay(50);
        queueCompleted.ShouldBeFalse();

        // Dequeue one item to make room
        await queue.DequeueTask(CancellationToken.None);

        // Wait for the blocked queue task to complete
        await Task.WhenAny(queueTask, Task.Delay(1000));

        // Assert
        queueCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task DequeueTask_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var queue = new TaskQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await queue.DequeueTask(cts.Token));
    }

    [Fact]
    public async Task Queue_PreservesOrder()
    {
        // Arrange
        var queue = new TaskQueue();
        var order = new List<int>();

        await queue.QueueTask(_ =>
        {
            order.Add(1);
            return Task.CompletedTask;
        }, CancellationToken.None);
        await queue.QueueTask(_ =>
        {
            order.Add(2);
            return Task.CompletedTask;
        }, CancellationToken.None);
        await queue.QueueTask(_ =>
        {
            order.Add(3);
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Act
        var task1 = await queue.DequeueTask(CancellationToken.None);
        var task2 = await queue.DequeueTask(CancellationToken.None);
        var task3 = await queue.DequeueTask(CancellationToken.None);

        await task1(CancellationToken.None);
        await task2(CancellationToken.None);
        await task3(CancellationToken.None);

        // Assert
        order.ShouldBe([1, 2, 3]);
    }
}