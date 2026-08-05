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

            var streamState = await messagingService.GetStreamStateAsync(topic.TopicId);

            // A null answer already means something real — there is no stream in progress —
            // so not live has to stay its own case rather than fold into the same return.
            if (!streamState.IsLive)
            {
                return;
            }

            var state = streamState.Value;
            if (state is null || state is { IsProcessing: false, BufferedMessages.Count: 0 })
            {
                return;
            }

            if (!messagesStore.State.MessagesByTopic.ContainsKey(topic.TopicId))
            {
                var history = await topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
                if (!history.IsLive)
                {
                    return;
                }

                pipeline.LoadHistory(topic.TopicId, history.Value!);
            }

            // The server's answer is the whole truth for this conversation, so it both surfaces
            // a prompt this client never saw and takes away one that was answered or timed out
            // while it was disconnected. A read that could not be made says nothing either way.
            var pendingApproval = await approvalService.GetPendingApprovalForTopicAsync(topic.TopicId);
            if (pendingApproval.IsLive)
            {
                dispatcher.Dispatch(new TopicApprovalsReconciled(topic.TopicId, pendingApproval.Value));
            }

            // Single rebuild: buffer + history → merged result
            var existingHistory = messagesStore.State.MessagesByTopic
                .GetValueOrDefault(topic.TopicId) ?? [];
            var result = BufferRebuildUtility.ResumeFromBuffer(
                state.BufferedMessages, existingHistory, state.CurrentPrompt, state.CurrentSenderId);

            // Start streaming FIRST (dispatches StreamStarted which creates empty StreamingContent)
            // Then ResumeFromBuffer fills it with content via StreamChunk
            // Order matters: StreamStarted resets content, so it must come before StreamChunk
            await streamingService.TryStartResumeStreamAsync(topic, result.StreamingMessage, state.CurrentMessageId);

            pipeline.ResumeFromBuffer(result, topic.TopicId, state.CurrentMessageId);
        }
        finally
        {
            dispatcher.Dispatch(new StopResuming(topic.TopicId));
        }
    }
}