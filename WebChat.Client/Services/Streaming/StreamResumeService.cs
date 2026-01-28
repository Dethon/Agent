using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamResumeService(
    IChatMessagingService messagingService,
    ITopicService topicService,
    IApprovalService approvalService,
    IStreamingService streamingService,
    IDispatcher dispatcher,
    IMessagePipeline pipeline,
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

            // Load history if not already in store
            if (!messagesStore.State.MessagesByTopic.ContainsKey(topic.TopicId))
            {
                var history = await topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
                pipeline.LoadHistory(topic.TopicId, history);
            }

            var pendingApproval = await approvalService.GetPendingApprovalForTopicAsync(topic.TopicId);
            if (pendingApproval is not null)
            {
                dispatcher.Dispatch(new ShowApproval(topic.TopicId, pendingApproval));
            }

            pipeline.ResumeFromBuffer(
                topic.TopicId,
                state.BufferedMessages,
                state.CurrentMessageId,
                state.CurrentPrompt,
                state.CurrentSenderId);

            // Use TryStartResumeStreamAsync to atomically check and start the stream
            // This prevents race conditions with SendMessageAsync
            var (_, streamingMessage) = BufferRebuildUtility.RebuildFromBuffer(state.BufferedMessages, []);
            await streamingService.TryStartResumeStreamAsync(topic, streamingMessage, state.CurrentMessageId);
        }
        finally
        {
            dispatcher.Dispatch(new StopResuming(topic.TopicId));
        }
    }
}