using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamResumeService(
    IChatMessagingService messagingService,
    ITopicService topicService,
    IChatStateManager stateManager,
    IApprovalService approvalService,
    IStreamingCoordinator streamingCoordinator,
    IDispatcher dispatcher,
    StreamingStore streamingStore) : IStreamResumeService
{
    private Func<Task>? _renderCallback;

    public void SetRenderCallback(Func<Task> callback)
    {
        _renderCallback = callback;
    }

    public async Task TryResumeStreamAsync(StoredTopic topic)
    {
        // Check if already resuming via store state
        if (streamingStore.State.ResumingTopics.Contains(topic.TopicId))
        {
            return;
        }

        dispatcher.Dispatch(new StartResuming(topic.TopicId));

        try
        {
            // Check if topic is already streaming via store
            if (streamingStore.State.StreamingTopics.Contains(topic.TopicId))
            {
                return;
            }

            var state = await messagingService.GetStreamStateAsync(topic.TopicId);
            if (state is null || state is { IsProcessing: false, BufferedMessages.Count: 0 })
            {
                return;
            }

            if (!stateManager.HasMessagesForTopic(topic.TopicId))
            {
                var history = await topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
                var messages = history.Select(h => new ChatMessageModel
                {
                    Role = h.Role,
                    Content = h.Content
                }).ToList();
                dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
                await NotifyRender();
            }

            dispatcher.Dispatch(new StreamStarted(topic.TopicId));
            var existingMessages = stateManager.GetMessagesForTopic(topic.TopicId);

            if (!string.IsNullOrEmpty(state.CurrentPrompt))
            {
                var promptExists = existingMessages.Any(m =>
                    m.Role == "user" && m.Content == state.CurrentPrompt);

                if (!promptExists)
                {
                    existingMessages.Add(new ChatMessageModel
                    {
                        Role = "user",
                        Content = state.CurrentPrompt
                    });
                }
            }

            var pendingApproval = await approvalService.GetPendingApprovalForTopicAsync(topic.TopicId);
            if (pendingApproval is not null)
            {
                dispatcher.Dispatch(new ShowApproval(topic.TopicId, pendingApproval));
            }

            var historyContent = existingMessages
                .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
                .Select(m => m.Content)
                .ToHashSet();

            var (completedTurns, streamingMessage) = streamingCoordinator.RebuildFromBuffer(
                state.BufferedMessages, historyContent);

            existingMessages.AddRange(completedTurns.Where(t => t.HasContent));

            streamingMessage = streamingCoordinator.StripKnownContent(streamingMessage, historyContent);
            dispatcher.Dispatch(new StreamChunk(
                topic.TopicId,
                streamingMessage.Content,
                streamingMessage.Reasoning,
                streamingMessage.ToolCalls,
                null));

            await NotifyRender();

            await streamingCoordinator.ResumeStreamResponseAsync(
                topic, streamingMessage, state.CurrentMessageId, NotifyRender);
        }
        finally
        {
            dispatcher.Dispatch(new StopResuming(topic.TopicId));
        }
    }

    private async Task NotifyRender()
    {
        if (_renderCallback is not null)
        {
            await _renderCallback();
        }
    }
}
