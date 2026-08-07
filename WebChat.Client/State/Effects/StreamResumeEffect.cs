using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class StreamResumeEffect : IDisposable
{
    private readonly IDisposable _handlerRegistration;

    public StreamResumeEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        TopicStreams topicStreams,
        IStreamResumeService streamResumeService,
        ILogger<StreamResumeEffect> logger)
    {
        _handlerRegistration = dispatcher.RegisterHandler<RemoteStreamStarted>(action =>
        {
            var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == action.TopicId);

            // A topic we do not know about has nothing to resume, and one already resuming would
            // be resumed twice. Marking either as streaming here would be a stream nothing is
            // tracking, which is the shape this client no longer has a way to create.
            if (topic is null || topicStreams.Snapshot(action.TopicId).IsResuming)
            {
                return;
            }

            // Detached on purpose: a resumed stream is long-lived, so awaiting it would mean
            // awaiting the conversation.
            streamResumeService.TryResumeStreamAsync(topic).LogFaults(logger, nameof(RemoteStreamStarted));
        });
    }

    public void Dispose() => _handlerRegistration.Dispose();
}