using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatMessengerClient(
    WebChatSessionManager sessionManager,
    WebChatStreamManager streamManager,
    WebChatApprovalManager approvalManager,
    ChatThreadResolver threadResolver,
    IThreadStateStore threadStateStore,
    INotifier hubNotifier,
    ILogger<WebChatMessengerClient> logger) : IChatMessengerClient, IDisposable
{
    private readonly Channel<ChatPrompt> _promptChannel = Channel.CreateUnbounded<ChatPrompt>();
    private int _messageIdCounter;
    private bool _disposed;

    public bool SupportsScheduledNotifications => true;

    public MessageSource Source => MessageSource.WebUi;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var prompt in _promptChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return prompt;
        }
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
    {
        await foreach (var (key, update, _, _) in updates.WithCancellation(cancellationToken))
        {
            var topicId = sessionManager.GetTopicIdByChatId(key.ChatId);
            if (topicId is null)
            {
                logger.LogWarning("ProcessResponseStreamAsync: chatId {ChatId} not found", key.ChatId);
                continue;
            }

            try
            {
                foreach (var content in update.Contents)
                {
                    // StreamCompleteContent signals that one prompt's agent turn is done
                    // Only send IsComplete when ALL pending prompts are processed
                    if (content is StreamCompleteContent)
                    {
                        if (streamManager.DecrementPendingAndCheckIfShouldComplete(topicId))
                        {
                            var message = new ChatStreamMessage { IsComplete = true, MessageId = update.MessageId };
                            var notification = new StreamChangedNotification(StreamChangeType.Completed, topicId);

                            await streamManager.WriteMessageAsync(topicId, message, cancellationToken);
                            streamManager.CompleteStream(topicId);
                            await hubNotifier
                                .NotifyStreamChangedAsync(notification, cancellationToken)
                                .SafeAwaitAsync(
                                    logger,
                                    "Failed to notify stream completed for topic {TopicId}",
                                    topicId);
                        }

                        continue;
                    }

                    var timestamp = update.GetTimestamp();
                    var msg = content switch
                    {
                        TextContent tc when !string.IsNullOrEmpty(tc.Text) =>
                            new ChatStreamMessage
                                { Content = tc.Text, MessageId = update.MessageId, Timestamp = timestamp },
                        TextReasoningContent rc when !string.IsNullOrEmpty(rc.Text) =>
                            new ChatStreamMessage
                                { Reasoning = rc.Text, MessageId = update.MessageId, Timestamp = timestamp },
                        ErrorContent ec =>
                            new ChatStreamMessage { IsComplete = true, Error = ec.Message },
                        _ => null
                    };

                    if (msg is not null)
                    {
                        await streamManager.WriteMessageAsync(topicId, msg, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                await streamManager.WriteMessageAsync(
                    topicId,
                    new ChatStreamMessage { IsComplete = true, Error = ex.Message, MessageId = update.MessageId },
                    CancellationToken.None);

                if (streamManager.DecrementPendingAndCheckIfShouldComplete(topicId))
                {
                    streamManager.CompleteStream(topicId);

                    await hubNotifier.NotifyStreamChangedAsync(
                            new StreamChangedNotification(StreamChangeType.Completed, topicId),
                            CancellationToken.None)
                        .SafeAwaitAsync(logger, "Failed to notify stream completed for topic {TopicId}", topicId);
                }
            }
        }
    }

    public async Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
    {
        var topicId = TopicIdHasher.GenerateTopicId();
        var threadId = TopicIdHasher.GetThreadIdForTopic(topicId);

        var topic = new TopicMetadata(
            TopicId: topicId,
            ChatId: chatId,
            ThreadId: threadId,
            AgentId: agentId ?? "unknown",
            Name: name,
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);

        await threadStateStore.SaveTopicAsync(topic);

        await hubNotifier.NotifyTopicChangedAsync(
            new TopicChangedNotification(TopicChangeType.Created, topicId, topic), cancellationToken);

        sessionManager.StartSession(topicId, agentId ?? "unknown", chatId, threadId);

        return (int)threadId;
    }

    public async Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return false;
        }

        var topic = await threadStateStore.GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId,
            cancellationToken);
        return topic is not null;
    }

    public async Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            throw new ArgumentException("agentId is required for WebChat", nameof(agentId));
        }

        string? topicId;
        long actualChatId;
        long actualThreadId;

        TopicMetadata? existingTopic = null;
        if (threadId.HasValue && chatId.HasValue)
        {
            existingTopic = await threadStateStore.GetTopicByChatIdAndThreadIdAsync(
                agentId, chatId.Value, threadId.Value, ct);

            if (existingTopic is not null)
            {
                sessionManager.StartSession(existingTopic.TopicId, existingTopic.AgentId,
                    existingTopic.ChatId, existingTopic.ThreadId);
                topicId = existingTopic.TopicId;
                actualChatId = existingTopic.ChatId;
                actualThreadId = existingTopic.ThreadId;
            }
            else
            {
                actualChatId = chatId.Value;
                actualThreadId = await CreateThread(actualChatId, topicName ?? "External message", agentId, ct);
                topicId = sessionManager.GetTopicIdByChatId(actualChatId);
            }
        }
        else
        {
            actualChatId = chatId ?? GenerateChatId();
            actualThreadId = await CreateThread(actualChatId, topicName ?? "External message", agentId, ct);
            topicId = sessionManager.GetTopicIdByChatId(actualChatId);
        }

        if (topicId is null)
        {
            return new AgentKey(actualChatId, actualThreadId, agentId);
        }

        // Notify WebUI about the topic first (if it was an existing topic not created by CreateThread)
        // This ensures the client has the topic before the stream notification arrives
        if (existingTopic is not null)
        {
            await hubNotifier.NotifyTopicChangedAsync(
                    new TopicChangedNotification(TopicChangeType.Created, topicId, existingTopic), ct)
                .SafeAwaitAsync(logger, "Failed to notify topic created for topic {TopicId}", topicId);
        }

        // Ensure stream exists. For new streams, increment pending and notify clients.
        // For existing streams (e.g., WebUI where EnqueuePromptAndGetResponses already created it),
        // skip to avoid double-increment.
        var (_, _, isNewStream) = streamManager.GetOrCreateStream(topicId, topicName ?? "", null, ct);
        if (isNewStream)
        {
            streamManager.TryIncrementPending(topicId);
            await hubNotifier.NotifyStreamChangedAsync(
                    new StreamChangedNotification(StreamChangeType.Started, topicId), ct)
                .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
        }

        return new AgentKey(actualChatId, actualThreadId, agentId);
    }

    private static long GenerateChatId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
    {
        var topicId = sessionManager.GetTopicIdByChatId(agentKey.ChatId);
        if (topicId is null)
        {
            logger.LogWarning("StartScheduledStreamAsync: topicId not found for chatId={ChatId}", agentKey.ChatId);
            return;
        }

        streamManager.GetOrCreateStream(topicId, "Scheduled task", null, ct);
        streamManager.TryIncrementPending(topicId);

        await hubNotifier.NotifyStreamChangedAsync(
                new StreamChangedNotification(StreamChangeType.Started, topicId), ct)
            .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
    }

    public bool StartSession(string topicId, string agentId, long chatId, long threadId)
    {
        return sessionManager.StartSession(topicId, agentId, chatId, threadId);
    }

    public bool TryGetSession(string topicId, out WebChatSession? session)
    {
        return sessionManager.TryGetSession(topicId, out session);
    }

    public void EndSession(string topicId)
    {
        // Get session before removing it so we can cancel the agent
        if (sessionManager.TryGetSession(topicId, out var session) && session is not null)
        {
            var agentKey = new AgentKey(session.ChatId, session.ThreadId, session.AgentId);
            threadResolver.Cancel(agentKey);
        }

        sessionManager.EndSession(topicId);
        streamManager.CancelStream(topicId);
        approvalManager.CancelPendingApprovalsForTopic(topicId);
    }

    public async IAsyncEnumerable<ChatStreamMessage> EnqueuePromptAndGetResponses(
        string topicId,
        string message,
        string sender,
        string? correlationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!sessionManager.TryGetSession(topicId, out var session) || session is null)
        {
            logger.LogWarning("EnqueuePromptAndGetResponses: session not found for topicId={TopicId}", topicId);
            yield break;
        }

        // GetOrCreateStream returns existing channel if one exists, allowing multiple browsers
        // to share the same stream. IsNew indicates whether we created a new stream.
        var (broadcastChannel, linkedToken, isNewStream) =
            streamManager.GetOrCreateStream(topicId, message, sender, cancellationToken);
        streamManager.TryIncrementPending(topicId);

        // Write user message to buffer for other browsers to see on refresh
        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(sender, timestamp)
        };
        await streamManager.WriteMessageAsync(topicId, userMessage, cancellationToken);

        // Notify other browsers about the user message
        await hubNotifier.NotifyUserMessageAsync(
                new UserMessageNotification(topicId, message, sender, timestamp, correlationId), cancellationToken)
            .SafeAwaitAsync(logger, "Failed to notify user message for topic {TopicId}", topicId);

        // Only notify StreamChanged.Started for new streams
        // (don't send duplicate Started notifications when joining existing stream)
        if (isNewStream)
        {
            await hubNotifier.NotifyStreamChangedAsync(
                    new StreamChangedNotification(StreamChangeType.Started, topicId), cancellationToken)
                .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);
        }

        var messageId = Interlocked.Increment(ref _messageIdCounter);

        var prompt = new ChatPrompt
        {
            Prompt = message,
            ChatId = session.ChatId,
            ThreadId = (int)session.ThreadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = session.AgentId,
            Source = MessageSource.WebUi
        };

        _promptChannel.Writer.TryWrite(prompt);

        await foreach (var msg in broadcastChannel.Subscribe().ReadAllAsync(linkedToken))
        {
            yield return msg;
        }
    }

    public bool EnqueuePrompt(string topicId, string message, string sender, string? correlationId)
    {
        if (!sessionManager.TryGetSession(topicId, out var session) || session is null)
        {
            return false;
        }

        if (!streamManager.TryIncrementPending(topicId))
        {
            return false;
        }

        // Write user message to buffer for other browsers to see on refresh
        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(sender, timestamp)
        };
        // Fire and forget - don't block the enqueue
        _ = streamManager.WriteMessageAsync(topicId, userMessage, CancellationToken.None)
            .SafeAwaitAsync(logger, "Failed to buffer user message for topic {TopicId}", topicId);

        // Notify other browsers about the user message
        _ = hubNotifier.NotifyUserMessageAsync(
                new UserMessageNotification(topicId, message, sender, timestamp, correlationId), CancellationToken.None)
            .SafeAwaitAsync(logger, "Failed to notify user message for topic {TopicId}", topicId);

        var messageId = Interlocked.Increment(ref _messageIdCounter);

        var prompt = new ChatPrompt
        {
            Prompt = message,
            ChatId = session.ChatId,
            ThreadId = (int)session.ThreadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = session.AgentId,
            Source = MessageSource.WebUi
        };

        _promptChannel.Writer.TryWrite(prompt);
        return true;
    }

    public bool IsProcessing(string topicId)
    {
        return streamManager.IsStreaming(topicId);
    }

    public StreamState? GetStreamState(string topicId)
    {
        return streamManager.GetStreamState(topicId);
    }

    public IAsyncEnumerable<ChatStreamMessage>? SubscribeToStream(string topicId, CancellationToken cancellationToken)
    {
        return streamManager.SubscribeToStream(topicId, cancellationToken);
    }

    public void CancelProcessing(string topicId)
    {
        if (sessionManager.TryGetSession(topicId, out var session) && session is not null)
        {
            var agentKey = new AgentKey(session.ChatId, session.ThreadId, session.AgentId);
            threadResolver.Cancel(agentKey);
        }

        streamManager.CancelStream(topicId);
    }

    public bool IsApprovalPending(string approvalId)
    {
        return approvalManager.IsApprovalPending(approvalId);
    }

    public ToolApprovalRequestMessage? GetPendingApprovalForTopic(string topicId)
    {
        return approvalManager.GetPendingApprovalForTopic(topicId);
    }

    public Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        return approvalManager.RespondToApprovalAsync(approvalId, result);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _promptChannel.Writer.Complete();
        streamManager.Dispose();
    }
}