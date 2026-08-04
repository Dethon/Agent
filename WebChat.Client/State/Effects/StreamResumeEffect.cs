using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class StreamResumeEffect : IDisposable
{
    private readonly IDisposable _handlerRegistration;

    public StreamResumeEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        IStreamResumeService streamResumeService,
        ILogger<StreamResumeEffect> logger)
    {
        _handlerRegistration = dispatcher.RegisterHandler<RemoteStreamStarted>(action =>
        {
            var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == action.TopicId);

            // A topic we do not know about has nothing to resume, and one already resuming
            // would be resumed twice. Both cases just mark the stream as started.
            if (topic is null || streamingStore.State.ResumingTopics.Contains(action.TopicId))
            {
                dispatcher.Dispatch(new StreamStarted(action.TopicId));
                return;
            }

            // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean
            // awaiting the conversation.
            streamResumeService.TryResumeStreamAsync(topic).LogFaults(logger, nameof(RemoteStreamStarted));
        });
    }

    public void Dispose() => _handlerRegistration.Dispose();
}