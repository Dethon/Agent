using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatMessengerClient(
    WebChatSessionManager sessionManager,
    WebChatStreamManager streamManager,
    WebChatApprovalManager approvalManager,
    ChatThreadResolver threadResolver,
    INotifier hubNotifier,
    ILogger<WebChatMessengerClient> logger) : IChatMessengerClient, IDisposable
{
    private readonly Channel<ChatPrompt> _promptChannel = Channel.CreateUnbounded<ChatPrompt>();
    private int _messageIdCounter;
    private bool _disposed;

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
        IAsyncEnumerable<(AgentKey, AgentRunResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        await foreach (var (key, update, _) in updates.WithCancellation(cancellationToken))
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

                    var msg = content switch
                    {
                        TextContent tc when !string.IsNullOrEmpty(tc.Text) =>
                            new ChatStreamMessage { Content = tc.Text, MessageId = update.MessageId },
                        TextReasoningContent rc when !string.IsNullOrEmpty(rc.Text) =>
                            new ChatStreamMessage { Reasoning = rc.Text, MessageId = update.MessageId },
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
                            new StreamChangedNotification(StreamChangeType.Completed, topicId), CancellationToken.None)
                        .SafeAwaitAsync(logger, "Failed to notify stream completed for topic {TopicId}", topicId);
                }
            }
        }
    }

    public Task<int> CreateThread(long chatId, string name, string? botTokenHash, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? botTokenHash,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!sessionManager.TryGetSession(topicId, out var session) || session is null)
        {
            logger.LogWarning("EnqueuePromptAndGetResponses: session not found for topicId={TopicId}", topicId);
            yield break;
        }

        var (broadcastChannel, linkedToken) = streamManager.CreateStream(topicId, message, sender, cancellationToken);
        streamManager.TryIncrementPending(topicId);

        // Write user message to buffer for other browsers to see on refresh
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(sender)
        };
        await streamManager.WriteMessageAsync(topicId, userMessage, cancellationToken);

        // Notify other browsers about the user message
        await hubNotifier.NotifyUserMessageAsync(
                new UserMessageNotification(topicId, message, sender), cancellationToken)
            .SafeAwaitAsync(logger, "Failed to notify user message for topic {TopicId}", topicId);

        await hubNotifier.NotifyStreamChangedAsync(
                new StreamChangedNotification(StreamChangeType.Started, topicId), cancellationToken)
            .SafeAwaitAsync(logger, "Failed to notify stream started for topic {TopicId}", topicId);

        var messageId = Interlocked.Increment(ref _messageIdCounter);

        var prompt = new ChatPrompt
        {
            Prompt = message,
            ChatId = session.ChatId,
            ThreadId = (int)session.ThreadId,
            MessageId = messageId,
            Sender = sender,
            BotTokenHash = session.AgentId
        };

        _promptChannel.Writer.TryWrite(prompt);

        await foreach (var msg in broadcastChannel.Subscribe().ReadAllAsync(linkedToken))
        {
            yield return msg;
        }
    }

    public bool EnqueuePrompt(string topicId, string message, string sender)
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
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(sender)
        };
        // Fire and forget - don't block the enqueue
        _ = streamManager.WriteMessageAsync(topicId, userMessage, CancellationToken.None)
            .SafeAwaitAsync(logger, "Failed to buffer user message for topic {TopicId}", topicId);

        // Notify other browsers about the user message
        _ = hubNotifier.NotifyUserMessageAsync(
                new UserMessageNotification(topicId, message, sender), CancellationToken.None)
            .SafeAwaitAsync(logger, "Failed to notify user message for topic {TopicId}", topicId);

        var messageId = Interlocked.Increment(ref _messageIdCounter);

        var prompt = new ChatPrompt
        {
            Prompt = message,
            ChatId = session.ChatId,
            ThreadId = (int)session.ThreadId,
            MessageId = messageId,
            Sender = sender,
            BotTokenHash = session.AgentId
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