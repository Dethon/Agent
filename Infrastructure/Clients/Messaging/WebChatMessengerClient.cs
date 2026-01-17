using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
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

    public async Task SendResponse(
        long chatId,
        ChatResponseMessage responseMessage,
        long? threadId,
        string? botTokenHash,
        CancellationToken cancellationToken)
    {
        var topicId = sessionManager.GetTopicIdByChatId(chatId);
        if (topicId is null)
        {
            logger.LogWarning("SendResponse: chatId {ChatId} not found in sessions", chatId);
            return;
        }

        var streamMessage = new ChatStreamMessage
        {
            Content = responseMessage.Message,
            Reasoning = responseMessage.Reasoning,
            ToolCalls = responseMessage.CalledTools,
            Error = responseMessage.Error,
            IsComplete = responseMessage.IsComplete,
            MessageIndex = responseMessage.MessageIndex
        };

        await streamManager.WriteMessageAsync(topicId, streamMessage, cancellationToken);

        if (responseMessage.IsComplete)
        {
            streamManager.CompleteStream(topicId);
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