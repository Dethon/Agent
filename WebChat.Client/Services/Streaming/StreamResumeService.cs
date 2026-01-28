using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
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
            // Check if topic is already streaming via store (quick check before server call)
            if (streamingStore.State.StreamingTopics.Contains(topic.TopicId))
            {
                return;
            }

            // Check if streaming service has an active stream (atomic check with lock)
            if (await streamingService.IsStreamActiveAsync(topic.TopicId))
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
                var history = await topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
                var messages = history.Select(h => h.ToChatMessageModel()).ToList();
                dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
            }

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
                        Content = state.CurrentPrompt,
                        SenderId = state.CurrentSenderId
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

            // Skip user messages that match CurrentPrompt - already added above
            foreach (var turn in completedTurns.Where(t =>
                         t.HasContent && !(t.Role == "user" && t.Content == state.CurrentPrompt)))
            {
                dispatcher.Dispatch(new AddMessage(topic.TopicId, turn));
            }

            streamingMessage = BufferRebuildUtility.StripKnownContent(streamingMessage, historyContent);
            dispatcher.Dispatch(new StreamChunk(
                topic.TopicId,
                streamingMessage.Content,
                streamingMessage.Reasoning,
                streamingMessage.ToolCalls,
                string.IsNullOrEmpty(state.CurrentMessageId) ? null : state.CurrentMessageId));

            // Use TryStartResumeStreamAsync to atomically check and start the stream
            // This prevents race conditions with SendMessageAsync
            await streamingService.TryStartResumeStreamAsync(topic, streamingMessage, state.CurrentMessageId);
        }
        finally
        {
            dispatcher.Dispatch(new StopResuming(topic.TopicId));
        }
    }
}