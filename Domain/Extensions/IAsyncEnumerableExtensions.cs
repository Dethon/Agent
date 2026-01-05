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
    private sealed class AsyncGrouping<TKey, TElement>(TKey key, Action onComplete) : IAsyncGrouping<TKey, TElement>
    {
        private readonly Channel<TElement> _channel = Channel.CreateUnbounded<TElement>();
        private int _completed;

        public TKey Key => key;

        internal async ValueTask WriteAsync(TElement item, CancellationToken ct)
        {
            if (_completed != 0)
            {
                return;
            }

            try
            {
                await _channel.Writer.WriteAsync(item, ct);
            }
            catch (ChannelClosedException) { } // Group was completed concurrently, ignore
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

    extension<TSource>(IAsyncEnumerable<TSource> source)
    {
        public async IAsyncEnumerable<IAsyncGrouping<TKey, TSource>> GroupByStreaming<TKey>(
            Func<TSource, CancellationToken, ValueTask<TKey>> keySelector,
            [EnumeratorCancellation] CancellationToken ct = default) where TKey : notnull
        {
            var groups = new ConcurrentDictionary<TKey, AsyncGrouping<TKey, TSource>>();
            try
            {
                await foreach (var item in source.WithCancellation(ct))
                {
                    var key = await keySelector(item, ct);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new AsyncGrouping<TKey, TSource>(key, () => groups.TryRemove(key, out _));
                        groups[key] = group;
                        yield return group;
                    }

                    await group.WriteAsync(item, ct);
                }
            }
            finally
            {
                foreach (var group in groups.Values)
                {
                    group.Complete();
                }

                groups.Clear();
            }
        }

        public IAsyncEnumerable<TSource> Merge(IAsyncEnumerable<TSource> right, CancellationToken ct)
        {
            return new[] { source, right }.ToAsyncEnumerable().Merge(ct);
        }
    }

    public static IAsyncEnumerable<T> Merge<T>(this IEnumerable<IAsyncEnumerable<T>> sources, CancellationToken ct)
    {
        return sources.ToAsyncEnumerable().Merge(ct);
    }

    public static async IAsyncEnumerable<T> Merge<T>(
        this IAsyncEnumerable<IAsyncEnumerable<T>> sources,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _ = Pump(sources, channel.Writer, ct);
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }

    private static Task Pump<T>(
        IAsyncEnumerable<IAsyncEnumerable<T>> sources,
        ChannelWriter<T> writer,
        CancellationToken ct)
    {
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = 20
        };
        return Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(sources, options, async (stream, token) =>
                {
                    await foreach (var item in stream.WithCancellation(token))
                    {
                        await writer.WriteAsync(item, token);
                    }
                });

                writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                writer.TryComplete();
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex);
            }
        }, ct);
    }
}