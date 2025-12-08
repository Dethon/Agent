using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Domain.Extensions;

public static class IAsyncEnumerableExtensions
{
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