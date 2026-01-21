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
    IApprovalService approvalService,
    IStreamingService streamingService,
    IDispatcher dispatcher,
    MessagesStore messagesStore,
    StreamingStore streamingStore) : IStreamResumeService
{

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

            if (!messagesStore.State.MessagesByTopic.ContainsKey(topic.TopicId))
            {
                var history = await topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
                var messages = history.Select(h => new ChatMessageModel
                {
                    Role = h.Role,
                    Content = h.Content
                }).ToList();
                dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
            }

            dispatcher.Dispatch(new StreamStarted(topic.TopicId));
            var existingMessages = messagesStore.State.MessagesByTopic
                .GetValueOrDefault(topic.TopicId) ?? [];

            if (!string.IsNullOrEmpty(state.CurrentPrompt))
            {
                var promptExists = existingMessages.Any(m =>
                    m.Role == "user" && m.Content == state.CurrentPrompt);

                if (!promptExists)
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, new ChatMessageModel
                    {
                        Role = "user",
                        Content = state.CurrentPrompt
                    }));
                }
            }

            var pendingApproval = await approvalService.GetPendingApprovalForTopicAsync(topic.TopicId);
            if (pendingApproval is not null)
            {
                dispatcher.Dispatch(new ShowApproval(topic.TopicId, pendingApproval));
            }

            // Re-read after potential AddMessage dispatch
            existingMessages = messagesStore.State.MessagesByTopic
                .GetValueOrDefault(topic.TopicId) ?? [];

            var historyContent = existingMessages
                .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
                .Select(m => m.Content)
                .ToHashSet();

            var (completedTurns, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(
                state.BufferedMessages, historyContent);

            foreach (var turn in completedTurns.Where(t => t.HasContent))
            {
                dispatcher.Dispatch(new AddMessage(topic.TopicId, turn));
            }

            streamingMessage = BufferRebuildUtility.StripKnownContent(streamingMessage, historyContent);
            dispatcher.Dispatch(new StreamChunk(
                topic.TopicId,
                streamingMessage.Content,
                streamingMessage.Reasoning,
                streamingMessage.ToolCalls,
                null));

            await streamingService.ResumeStreamResponseAsync(topic, streamingMessage, state.CurrentMessageId);
        }
        finally
        {
            dispatcher.Dispatch(new StopResuming(topic.TopicId));
        }
    }
}
