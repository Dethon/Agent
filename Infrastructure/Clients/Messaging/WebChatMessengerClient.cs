using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatMessengerClient(
    WebChatSessionManager sessionManager,
    WebChatStreamManager streamManager,
    WebChatApprovalManager approvalManager,
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
        IAsyncEnumerable<(AgentKey, AgentRunResponseUpdate)> updates,
        CancellationToken cancellationToken)
    {
        await foreach (var (key, update) in updates.WithCancellation(cancellationToken))
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
                    var msg = content switch
                    {
                        TextContent tc when !string.IsNullOrEmpty(tc.Text) =>
                            new ChatStreamMessage { Content = tc.Text, MessageId = update.MessageId },
                        TextReasoningContent rc when !string.IsNullOrEmpty(rc.Text) =>
                            new ChatStreamMessage { Reasoning = rc.Text, MessageId = update.MessageId },
                        ErrorContent ec =>
                            new ChatStreamMessage { IsComplete = true, Error = ec.Message },
                        FunctionCallContent fc =>
                            new ChatStreamMessage
                            {
                                ToolCalls = $"{fc.Name}({JsonSerializer.Serialize(fc.Arguments)})",
                                MessageId = update.MessageId
                            },
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
        sessionManager.EndSession(topicId);
        streamManager.CancelStream(topicId);
        approvalManager.CancelPendingApprovalsForTopic(topicId);
    }

    public string? GetTopicIdByChatId(long chatId)
    {
        return sessionManager.GetTopicIdByChatId(chatId);
    }

    public IAsyncEnumerable<ChatStreamMessage> EnqueuePromptAndGetResponses(
        string topicId,
        string message,
        string sender,
        CancellationToken cancellationToken)
    {
        if (!sessionManager.TryGetSession(topicId, out var session) || session is null)
        {
            logger.LogWarning("EnqueuePromptAndGetResponses: session not found for topicId={TopicId}", topicId);
            return AsyncEnumerable.Empty<ChatStreamMessage>();
        }

        var (broadcastChannel, linkedToken) = streamManager.CreateStream(topicId, message, cancellationToken);

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
        return broadcastChannel.Subscribe().ReadAllAsync(linkedToken);
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

    public bool RespondToApproval(string approvalId, ToolApprovalResult result)
    {
        return approvalManager.RespondToApproval(approvalId, result);
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