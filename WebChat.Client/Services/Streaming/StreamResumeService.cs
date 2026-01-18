using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamResumeService(
    IChatMessagingService messagingService,
    ITopicService topicService,
    IChatStateManager stateManager,
    IApprovalService approvalService,
    IStreamingCoordinator streamingCoordinator) : IStreamResumeService
{
    private Func<Task>? _renderCallback;

    public void SetRenderCallback(Func<Task> callback)
    {
        _renderCallback = callback;
    }

    public async Task TryResumeStreamAsync(StoredTopic topic)
    {
        if (!stateManager.TryStartResuming(topic.TopicId))
        {
            return;
        }

        try
        {
            if (stateManager.IsTopicStreaming(topic.TopicId))
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
                stateManager.SetMessagesForTopic(topic.TopicId, messages);
                await NotifyRender();
            }

            stateManager.StartStreaming(topic.TopicId);
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
                stateManager.SetApprovalRequest(pendingApproval);
            }

            var historyContent = existingMessages
                .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
                .Select(m => m.Content)
                .ToHashSet();

            var (completedTurns, streamingMessage) = streamingCoordinator.RebuildFromBuffer(
                state.BufferedMessages, historyContent);

            existingMessages.AddRange(completedTurns.Where(t => t.HasContent));

            streamingMessage = streamingCoordinator.StripKnownContent(streamingMessage, historyContent);
            stateManager.UpdateStreamingMessage(topic.TopicId, streamingMessage);

            await NotifyRender();

            await streamingCoordinator.ResumeStreamResponseAsync(
                topic, streamingMessage, state.CurrentMessageId, NotifyRender);
        }
        finally
        {
            stateManager.StopResuming(topic.TopicId);
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