using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients;

public sealed class WebChatMessengerClient(ILogger<WebChatMessengerClient> logger)
    : IChatMessengerClient, IDisposable
{
    private readonly Channel<ChatPrompt> _promptChannel = Channel.CreateUnbounded<ChatPrompt>();
    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new();
    private readonly ConcurrentDictionary<long, string> _chatToTopic = new();
    private readonly ConcurrentDictionary<string, Channel<ChatStreamMessage>> _responseChannels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private int _messageIdCounter;

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
        if (!_chatToTopic.TryGetValue(chatId, out var topicId))
        {
            logger.LogWarning("SendResponse: chatId {ChatId} not found in _chatToTopic", chatId);
            return;
        }

        if (!_responseChannels.TryGetValue(topicId, out var channel))
        {
            logger.LogWarning("SendResponse: topicId {TopicId} not found in _responseChannels", topicId);
            return;
        }

        var streamMessage = new ChatStreamMessage
        {
            Content = responseMessage.Message,
            Reasoning = responseMessage.Reasoning,
            ToolCalls = responseMessage.CalledTools,
            IsComplete = responseMessage.IsComplete,
            MessageIndex = responseMessage.MessageIndex
        };

        await channel.Writer.WriteAsync(streamMessage, cancellationToken);

        if (responseMessage.IsComplete)
        {
            channel.Writer.Complete();
            _responseChannels.TryRemove(topicId, out _);
            _cancellationTokens.TryRemove(topicId, out _);
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
        var session = new WebChatSession(agentId, chatId, threadId);
        _sessions[topicId] = session;
        _chatToTopic[chatId] = topicId;
        return true;
    }

    public bool TryGetSession(string topicId, out WebChatSession? session)
    {
        return _sessions.TryGetValue(topicId, out session);
    }

    public void EndSession(string topicId)
    {
        if (_sessions.TryRemove(topicId, out var session))
        {
            _chatToTopic.TryRemove(session.ChatId, out _);
        }

        if (_responseChannels.TryRemove(topicId, out var channel))
        {
            channel.Writer.Complete();
        }

        if (!_cancellationTokens.TryRemove(topicId, out var cts))
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    public IAsyncEnumerable<ChatStreamMessage> EnqueuePromptAndGetResponses(
        string topicId,
        string message,
        string sender,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(topicId, out var session))
        {
            logger.LogWarning("EnqueuePromptAndGetResponses: session not found for topicId={TopicId}", topicId);
            return AsyncEnumerable.Empty<ChatStreamMessage>();
        }

        var responseChannel = Channel.CreateUnbounded<ChatStreamMessage>();
        _responseChannels[topicId] = responseChannel;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokens[topicId] = cts;

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
        return responseChannel.Reader.ReadAllAsync(cts.Token);
    }

    public bool IsProcessing(string topicId)
    {
        return _responseChannels.ContainsKey(topicId);
    }

    public void CancelProcessing(string topicId)
    {
        if (_cancellationTokens.TryRemove(topicId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_responseChannels.TryRemove(topicId, out var channel))
        {
            channel.Writer.Complete();
        }
    }

    public void Dispose()
    {
        _promptChannel.Writer.Complete();

        foreach (var channel in _responseChannels.Values)
        {
            channel.Writer.Complete();
        }

        foreach (var cts in _cancellationTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}

public record WebChatSession(string AgentId, long ChatId, long ThreadId);