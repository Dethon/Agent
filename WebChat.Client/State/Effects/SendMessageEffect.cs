using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services;
using WebChat.Client.Services.Utilities;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

public sealed class SendMessageEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly IChatSessionService _sessionService;
    private readonly IStreamingService _streamingService;
    private readonly ITopicService _topicService;
    private readonly IChatMessagingService _messagingService;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly IMessagePipeline _pipeline;

    public SendMessageEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        IChatSessionService sessionService,
        IStreamingService streamingService,
        ITopicService topicService,
        IChatMessagingService messagingService,
        UserIdentityStore userIdentityStore,
        IMessagePipeline pipeline)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _streamingStore = streamingStore;
        _sessionService = sessionService;
        _streamingService = streamingService;
        _topicService = topicService;
        _messagingService = messagingService;
        _userIdentityStore = userIdentityStore;
        _pipeline = pipeline;

        dispatcher.RegisterHandler<SendMessage>(HandleSendMessage);
        dispatcher.RegisterHandler<CancelStreaming>(HandleCancelStreaming);
    }

    private void HandleSendMessage(SendMessage action)
    {
        _ = HandleSendMessageAsync(action);
    }

    private void HandleCancelStreaming(CancelStreaming action)
    {
        _ = HandleCancelStreamingAsync(action.TopicId);
    }

    private async Task HandleCancelStreamingAsync(string topicId)
    {
        await _messagingService.CancelTopicAsync(topicId);
        _dispatcher.Dispatch(new StreamCancelled(topicId));
    }

    private async Task HandleSendMessageAsync(SendMessage action)
    {
        var state = _topicsStore.State;
        StoredTopic topic;

        if (string.IsNullOrEmpty(action.TopicId))
        {
            // Create new topic
            var topicName = action.Message.Length > 50 ? action.Message[..50] + "..." : action.Message;
            var topicId = TopicIdGenerator.GenerateTopicId();
            topic = new StoredTopic
            {
                TopicId = topicId,
                ChatId = TopicIdGenerator.GetChatIdForTopic(topicId),
                ThreadId = TopicIdGenerator.GetThreadIdForTopic(topicId),
                AgentId = state.SelectedAgentId!,
                Name = topicName,
                CreatedAt = DateTime.UtcNow
            };

            var success = await _sessionService.StartSessionAsync(topic);
            if (!success)
            {
                return;
            }

            _dispatcher.Dispatch(new AddTopic(topic));
            _dispatcher.Dispatch(new SelectTopic(topic.TopicId));
            _dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
            await _topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
        }
        else
        {
            topic = state.Topics.First(t => t.TopicId == action.TopicId);
            if (_sessionService.CurrentTopic?.TopicId != topic.TopicId)
            {
                await _sessionService.StartSessionAsync(topic);
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

        // Delegate to streaming service (handles stream reuse internally)
        _ = _streamingService.SendMessageAsync(topic, action.Message, correlationId);
    }

    public void Dispose()
    {
        // No subscription to dispose, handler is registered with dispatcher
    }
}