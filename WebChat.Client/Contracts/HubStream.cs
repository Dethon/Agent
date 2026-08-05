namespace WebChat.Client.Contracts;

// Opening a stream is the moment its transport has to prove it is alive, because that is the
// moment the caller gets an answer to branch on. A hub stream is lazy — asking for it touches
// nothing, and the first pull is what reaches the wire — so a stream handed back unopened
// carries its transport fault past the one place that turns such a fault into "not live", and
// it surfaces deep inside the consumer loop as a raw error in the transcript instead.
//
// So the first hop is taken here, inside the call the live connection wraps in its fault
// filter, and the opened stream replays it. Both the real hub connection and its fake open
// their streams through this, so the fake cannot drift back to answering before the wire does.
public static class HubStream
{
    public static async Task<IAsyncEnumerable<T>> OpenAsync<T>(IAsyncEnumerable<T> stream)
    {
        var enumerator = stream.GetAsyncEnumerator();
        try
        {
            var hasFirst = await enumerator.MoveNextAsync();
            return new OpenedStream<T>(enumerator, hasFirst);
        }
        catch
        {
            await enumerator.DisposeAsync();
            throw;
        }
    }

    private sealed class OpenedStream<T>(IAsyncEnumerator<T> source, bool hasFirst) : IAsyncEnumerable<T>
    {
        private int _taken;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _taken, 1) == 0
                ? new OpenedEnumerator<T>(source, hasFirst)
                : throw new InvalidOperationException("An opened hub stream can only be enumerated once.");
    }

    // The first item is already sitting in the source enumerator's Current, so the first
    // MoveNext here only has to answer whether there was one.
    private sealed class OpenedEnumerator<T>(IAsyncEnumerator<T> source, bool hasFirst) : IAsyncEnumerator<T>
    {
        private bool _firstPending = true;

        public T Current => source.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            if (!_firstPending)
            {
                return source.MoveNextAsync();
            }

            _firstPending = false;
            return ValueTask.FromResult(hasFirst);
        }

        public ValueTask DisposeAsync() => source.DisposeAsync();
    }
}