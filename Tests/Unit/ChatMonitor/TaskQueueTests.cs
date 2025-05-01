using Domain.ChatMonitor;
using Shouldly;

namespace Tests.Unit.ChatMonitor;

public class TaskQueueTests
{
    [Fact]
    public async Task QueueTask_ShouldEnqueueTask()
    {
        // given
        var queue = new TaskQueue();
        var taskExecuted = false;

        // when
        await queue.QueueTask(_ =>
        {
            taskExecuted = true;
            return Task.CompletedTask;
        });

        var dequeuedTask = await queue.DequeueTask(CancellationToken.None);
        await dequeuedTask(CancellationToken.None);

        // then
        taskExecuted.ShouldBeTrue();
    }

    [Fact]
    public async Task DequeueTask_ShouldReturnTasksInOrder()
    {
        // given
        var queue = new TaskQueue();
        var executionOrder = new List<int>();

        // when
        await queue.QueueTask(_ =>
        {
            executionOrder.Add(1);
            return Task.CompletedTask;
        });

        await queue.QueueTask(_ =>
        {
            executionOrder.Add(2);
            return Task.CompletedTask;
        });

        await queue.QueueTask(_ =>
        {
            executionOrder.Add(3);
            return Task.CompletedTask;
        });

        var task1 = await queue.DequeueTask(CancellationToken.None);
        var task2 = await queue.DequeueTask(CancellationToken.None);
        var task3 = await queue.DequeueTask(CancellationToken.None);

        await task1(CancellationToken.None);
        await task2(CancellationToken.None);
        await task3(CancellationToken.None);

        // then
        executionOrder.ShouldBe(new List<int>
        {
            1,
            2,
            3
        });
    }

    [Fact]
    public async Task TaskQueue_ShouldRespectCapacity()
    {
        // given
        const int capacity = 2;
        var queue = new TaskQueue(capacity);
        var capacityReachedTcs = new TaskCompletionSource<bool>();

        // when
        for (var i = 0; i < capacity; i++)
        {
            await queue.QueueTask(_ => Task.CompletedTask);
        }

        var blockingEnqueueTask = Task.Run(async () =>
        {
            await queue.QueueTask(_ => Task.CompletedTask);
            capacityReachedTcs.SetResult(true);
        });

        // then
        var delayTask = Task.Delay(500);
        var completedTask = await Task.WhenAny(capacityReachedTcs.Task, delayTask);
        completedTask.ShouldBe(delayTask, "The queue should be blocking");

        await queue.DequeueTask(CancellationToken.None);
        await blockingEnqueueTask;
        await Task.WhenAny(capacityReachedTcs.Task, Task.Delay(500));
        capacityReachedTcs.Task.IsCompleted.ShouldBeTrue("The queue should allow enqueuing after dequeuing");
    }

    [Fact]
    public async Task DequeueTask_ShouldRespectCancellation()
    {
        // given
        var queue = new TaskQueue();
        var cts = new CancellationTokenSource();

        // when/then
        var dequeueTask = queue.DequeueTask(cts.Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => dequeueTask.AsTask());
    }
}