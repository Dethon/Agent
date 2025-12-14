using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Domain.Extensions;

public interface IAsyncGrouping<out TKey, out TElement> : IAsyncEnumerable<TElement>
{
    TKey Key { get; }
    
    void Complete();
}

public static class IAsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<IAsyncGrouping<TKey, TSource>> GroupByStreaming<TSource, TKey>(
        this IAsyncEnumerable<TSource> source,
        Func<TSource, CancellationToken, ValueTask<TKey>> keySelector,
        [EnumeratorCancellation] CancellationToken ct = default) where TKey : notnull
    {
        var groups = new ConcurrentDictionary<TKey, AsyncGrouping<TKey, TSource>>();

        await foreach (var item in source.WithCancellation(ct))
        {
            var key = await keySelector(item, ct);
            var newGroup = new AsyncGrouping<TKey, TSource>(key, () =>
            {
                groups.TryRemove(key, out _);
            });
            var group = groups.GetOrAdd(key, newGroup);
            if (ReferenceEquals(group, newGroup))
            {
                yield return newGroup;
            }

            await group.WriteAsync(item, ct);
        }

        foreach (var group in groups.Values)
        {
            group.Complete();
        }
    }

    private sealed class AsyncGrouping<TKey, TElement>(TKey key, Action onComplete) : IAsyncGrouping<TKey, TElement>
    {
        private readonly Channel<TElement> _channel = Channel.CreateUnbounded<TElement>();
        private int _completed;
        
        public TKey Key => key;

        public ValueTask WriteAsync(TElement item, CancellationToken ct)
        {
            return _channel.Writer.WriteAsync(item, ct);
        }

        public void Complete()
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            {
                return;
            }

            _channel.Writer.TryComplete();
            onComplete();
        }

        public IAsyncEnumerator<TElement> GetAsyncEnumerator(CancellationToken ct = default)
        {
            return _channel.Reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        }
    }

    public static IAsyncEnumerable<T> Merge<T>(
        this IAsyncEnumerable<T> left,
        IAsyncEnumerable<T> right,
        CancellationToken ct)
    {
        return new[] { left, right }.ToAsyncEnumerable().Merge(ct);
    }

    public static IAsyncEnumerable<T> Merge<T>(this IEnumerable<IAsyncEnumerable<T>> sources, CancellationToken ct)
    {
        return sources.ToAsyncEnumerable().Merge(ct);
    }

    public static async IAsyncEnumerable<T> Merge<T>(
        this IAsyncEnumerable<IAsyncEnumerable<T>> sources,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<T>();
        var writer = channel.Writer;

        _ = Task.Run(() => startPumps(sources), ct)
            .ContinueWith(
                t => t.IsFaulted ? writer.TryComplete(t.Exception) : writer.TryComplete(),
                TaskScheduler.Default);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }

        yield break;

        async Task startPumps(IAsyncEnumerable<IAsyncEnumerable<T>> streams)
        {
            await Task.WhenAll(await streams.Select(pump).ToArrayAsync(ct));
        }

        async Task pump(IAsyncEnumerable<T> source)
        {
            await foreach (var item in source.WithCancellation(ct))
            {
                await writer.WriteAsync(item, ct);
            }
        }
    }
}