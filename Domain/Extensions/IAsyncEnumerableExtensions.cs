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
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    writer.TryComplete(t.Exception);
                }
                else
                {
                    writer.TryComplete();
                }
            }, TaskScheduler.Default);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }

        yield break;

        async Task pump(IAsyncEnumerable<T> source)
        {
            try
            {
                await foreach (var item in source.WithCancellation(ct))
                {
                    await writer.WriteAsync(item, ct);
                }
            }
            catch (Exception ex)
            {
                writer.TryComplete(ex);
            }
        }
    }
}