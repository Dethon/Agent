using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Domain.Extensions;

public static class IAsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> Merge<T>(
        this IAsyncEnumerable<T> left,
        IAsyncEnumerable<T> right,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<T>();
        var writer = channel.Writer;

        _ = Task.WhenAll(pump(left), pump(right))
            .ContinueWith(_ => writer.Complete(), TaskScheduler.Default);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }

        yield break;

        async Task pump(IAsyncEnumerable<T> source)
        {
            await foreach (var item in source.WithCancellation(ct))
            {
                await writer.WriteAsync(item, ct);
            }
        }
    }
}