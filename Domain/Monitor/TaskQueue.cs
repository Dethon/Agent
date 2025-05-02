using System.Threading.Channels;

namespace Domain.Monitor;

public class TaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue;

    public TaskQueue(int capacity = 10)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(options);
    }

    public ValueTask QueueTask(Func<CancellationToken, Task> workItem)
    {
        return _queue.Writer.WriteAsync(workItem);
    }

    public ValueTask<Func<CancellationToken, Task>> DequeueTask(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAsync(cancellationToken);
    }
}