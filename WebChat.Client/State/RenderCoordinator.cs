using System.Reactive.Linq;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State;

/// <summary>
/// Provides throttled observables for streaming content using 50ms Sample intervals.
/// This is the single point where render throttling is applied - consumers should NOT
/// apply additional throttling.
/// </summary>
public sealed class RenderCoordinator : IDisposable
{
    private static readonly TimeSpan _renderInterval = TimeSpan.FromMilliseconds(50);
    private readonly StreamingStore _streamingStore;

    public RenderCoordinator(StreamingStore streamingStore)
    {
        ArgumentNullException.ThrowIfNull(streamingStore);
        _streamingStore = streamingStore;
    }

    /// <summary>
    /// Creates a throttled observable for streaming content of a specific topic.
    /// Emits at most once per 50ms with the latest value.
    /// </summary>
    public IObservable<StreamingContent?> CreateStreamingObservable(string topicId)
    {
        ArgumentNullException.ThrowIfNull(topicId);

        return _streamingStore.StateObservable
            .Select(StreamingSelectors.SelectStreamingContent(topicId))
            .Sample(_renderInterval)
            .DistinctUntilChanged();
    }

    /// <summary>
    /// Creates a throttled observable for whether a specific topic is streaming.
    /// Emits at most once per 50ms with the latest value.
    /// </summary>
    public IObservable<bool> CreateIsStreamingObservable(string topicId)
    {
        ArgumentNullException.ThrowIfNull(topicId);

        return _streamingStore.StateObservable
            .Select(StreamingSelectors.SelectIsStreaming(topicId))
            .Sample(_renderInterval)
            .DistinctUntilChanged();
    }

    public void Dispose()
    {
        // RenderCoordinator does not own any subscriptions - callers own their subscriptions.
        // Dispose exists for consistency with other services and future extensibility.
    }
}