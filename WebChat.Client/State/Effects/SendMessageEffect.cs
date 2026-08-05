using Domain.Conversations;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class SendMessageEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly MessagesStore _messagesStore;
    private readonly IChatSessionService _sessionService;
    private readonly IStreamingService _streamingService;
    private readonly ITopicService _topicService;
    private readonly IChatMessagingService _messagingService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly IMessagePipeline _pipeline;
    private readonly SpaceStore _spaceStore;
    private readonly ILogger<SendMessageEffect> _logger;

    public SendMessageEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        MessagesStore messagesStore,
        IChatSessionService sessionService,
        IStreamingService streamingService,
        ITopicService topicService,
        IChatMessagingService messagingService,
        UserIdentityStore userIdentityStore,
        IMessagePipeline pipeline,
        SpaceStore spaceStore,
        ILogger<SendMessageEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _streamingStore = streamingStore;
        _messagesStore = messagesStore;
        _sessionService = sessionService;
        _streamingService = streamingService;
        _topicService = topicService;
        _messagingService = messagingService;
        _userIdentityStore = userIdentityStore;
        _pipeline = pipeline;
        _spaceStore = spaceStore;
        _logger = logger;

        dispatcher.RegisterHandler<SendMessage>(action =>
            HandleSendMessageAsync(action).LogFaults(_logger, nameof(SendMessage)));
        dispatcher.RegisterHandler<CancelStreaming>(action =>
            HandleCancelStreamingAsync(action.TopicId).LogFaults(_logger, nameof(CancelStreaming)));
        dispatcher.RegisterHandler<RetryLastMessage>(HandleRetryLastMessage);
    }

    private async Task HandleCancelStreamingAsync(string topicId)
    {
        var cancelled = await _messagingService.CancelTopicAsync(topicId);

        // Marking the reply stopped when the stop never reached the server would surprise the
        // user the moment it carries on.
        if (!cancelled.IsLive)
        {
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        _dispatcher.Dispatch(new StreamCancelled(topicId));
    }

    private async Task HandleSendMessageAsync(SendMessage action)
    {
        try
        {
            await SendAsync(action);
        }
        catch
        {
            // The message is already rendered locally, so a fault here means it never reached
            // the server. Same feedback as the not-live branches; the rethrow is what LogFaults
            // observes.
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            throw;
        }
    }

    private async Task SendAsync(SendMessage action)
    {
        var state = _topicsStore.State;
        StoredTopic topic;

        if (string.IsNullOrEmpty(action.TopicId))
        {
            var topicName = action.Message.Length > 50 ? action.Message[..50] + "..." : action.Message;
            var identity = ConversationIdGenerator.Create();
            topic = new StoredTopic
            {
                TopicId = identity.TopicId,
                ChatId = identity.ChatId,
                ThreadId = identity.ThreadId,
                AgentId = state.SelectedAgentId!,
                Name = topicName,
                CreatedAt = DateTime.UtcNow,
                SpaceSlug = _spaceStore.State.CurrentSlug
            };

            var started = await _sessionService.StartSessionAsync(topic);

            // Three outcomes, not two. A server that refuses is live and has answered, and
            // stays as silent as it is today; a call that could not be made says so once.
            if (!started.IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }

            if (!started.Value)
            {
                return;
            }

            _dispatcher.Dispatch(new AddTopic(topic));
            _dispatcher.Dispatch(new SelectTopic(topic.TopicId));
            _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));

            // No early return, unlike the other user actions: the conversation is already on
            // screen, so the send still runs and its own failure toast dedupes into this one.
            var saved = await _topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
            if (!saved.IsLive)
            {
                _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            }
        }
        else
        {
            topic = state.Topics.First(t => t.TopicId == action.TopicId);
            if (_sessionService.CurrentTopic?.TopicId != topic.TopicId)
            {
                var started = await _sessionService.StartSessionAsync(topic);
                if (!started.IsLive)
                {
                    _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                    return;
                }
            }
        }

        // If streaming is active, finalize the current bubble before adding user message
        var streamingState = _streamingStore.State;
        if (streamingState.StreamingTopics.Contains(topic.TopicId))
        {
            var currentContent = streamingState.StreamingByTopic.GetValueOrDefault(topic.TopicId);
            if (currentContent?.HasContent == true)
            {
                _pipeline.FinalizeMessage(topic.TopicId, currentContent.CurrentMessageId);
            }
        }

        // Submit user message through pipeline (handles correlation tracking and AddMessage dispatch)
        var identityState = _userIdentityStore.State;
        var currentUser = identityState.AvailableUsers
            .FirstOrDefault(u => u.Id == identityState.SelectedUserId);

        var correlationId = _pipeline.SubmitUserMessage(topic.TopicId, action.Message, currentUser?.Id);

        // Delegate to streaming service (handles stream reuse internally). Awaited so a fault
        // opening the send lands in the catch above; the call returns once the stream is open,
        // not when the reply completes.
        await _streamingService.SendMessageAsync(topic, action.Message, correlationId);
    }

    private void HandleRetryLastMessage(RetryLastMessage action)
    {
        _dispatcher.Dispatch(new RemoveTrailingErrors(action.TopicId));

        var messages = _messagesStore.State.MessagesByTopic.GetValueOrDefault(action.TopicId, []);
        var lastUserMessage = messages.LastOrDefault(m => m.Role == "user");
        if (lastUserMessage is not null)
        {
            _dispatcher.Dispatch(new SendMessage(action.TopicId, lastUserMessage.Content));
        }
    }

    public void Dispose()
    {
        // No subscription to dispose, handler is registered with dispatcher
    }
}