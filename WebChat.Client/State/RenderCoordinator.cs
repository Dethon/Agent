using System.Reactive.Linq;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State;

public sealed class RenderCoordinator : IDisposable
{
    private static readonly TimeSpan _renderInterval = TimeSpan.FromMilliseconds(50);
    private readonly StreamingStore _streamingStore;

    public RenderCoordinator(StreamingStore streamingStore)
    {
        ArgumentNullException.ThrowIfNull(streamingStore);
        _streamingStore = streamingStore;
    }

    public void Dispose()
    {
        // RenderCoordinator does not own any subscriptions - callers own their subscriptions.
        // Dispose exists for consistency with other services and future extensibility.
    }


    public IObservable<StreamingContent?> CreateStreamingObservable(string topicId)
    {
        ArgumentNullException.ThrowIfNull(topicId);

        return _streamingStore.StateObservable
            .Select(StreamingSelectors.SelectStreamingContent(topicId))
            .Sample(_renderInterval)
            .DistinctUntilChanged();
    }


    public IObservable<bool> CreateIsStreamingObservable(string topicId)
    {
        ArgumentNullException.ThrowIfNull(topicId);

        return _streamingStore.StateObservable
            .Select(StreamingSelectors.SelectIsStreaming(topicId))
            .Sample(_renderInterval)
            .DistinctUntilChanged();
    }
}