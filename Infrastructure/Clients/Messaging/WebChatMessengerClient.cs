using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatMessengerClient(ILogger<WebChatMessengerClient> logger)
    : IChatMessengerClient, IDisposable
{
    private readonly Channel<ChatPrompt> _promptChannel = Channel.CreateUnbounded<ChatPrompt>();
    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new();
    private readonly ConcurrentDictionary<long, string> _chatToTopic = new();
    private readonly ConcurrentDictionary<string, BroadcastChannel<ChatStreamMessage>> _responseChannels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();
    private readonly ConcurrentDictionary<string, long> _sequenceCounters = new();
    private readonly ConcurrentDictionary<string, string> _currentPrompts = new();
    private int _messageIdCounter;
    private bool _disposed;

    private sealed class StreamBuffer
    {
        private readonly List<ChatStreamMessage> _messages = [];
        private readonly Lock _lock = new();
        private const int MaxBufferSize = 100;

        public void Add(ChatStreamMessage message)
        {
            lock (_lock)
            {
                if (_messages.Count >= MaxBufferSize)
                {
                    _messages.RemoveAt(0);
                }

                _messages.Add(message);
            }
        }

        public IReadOnlyList<ChatStreamMessage> GetAll()
        {
            lock (_lock)
            {
                return [.. _messages];
            }
        }
    }

    private sealed class BroadcastChannel<T>
    {
        private readonly List<Channel<T>> _subscribers = [];
        private readonly Lock _lock = new();

        public ChannelReader<T> Subscribe()
        {
            var channel = Channel.CreateUnbounded<T>();
            lock (_lock)
            {
                _subscribers.Add(channel);
            }

            return channel.Reader;
        }

        public async Task WriteAsync(T item, CancellationToken ct)
        {
            List<Channel<T>> subs;
            lock (_lock)
            {
                subs = [.. _subscribers];
            }

            await Task.WhenAll(subs.Select(s => s.Writer.WriteAsync(item, ct).AsTask()));
        }

        public void Complete()
        {
            lock (_lock)
            {
                foreach (var s in _subscribers)
                {
                    s.Writer.TryComplete();
                }

                _subscribers.Clear();
            }
        }
    }

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

        // Assign sequence number for deduplication
        var sequenceNumber = _sequenceCounters.AddOrUpdate(topicId, 1, (_, seq) => seq + 1);

        var streamMessage = new ChatStreamMessage
        {
            Content = responseMessage.Message,
            Reasoning = responseMessage.Reasoning,
            ToolCalls = responseMessage.CalledTools,
            Error = responseMessage.Error,
            IsComplete = responseMessage.IsComplete,
            MessageIndex = responseMessage.MessageIndex,
            SequenceNumber = sequenceNumber
        };

        // Buffer the message for late-joining clients
        if (!_streamBuffers.TryGetValue(topicId, out var buffer))
        {
            buffer = new StreamBuffer();
            _streamBuffers[topicId] = buffer;
        }

        buffer.Add(streamMessage);

        await channel.WriteAsync(streamMessage, cancellationToken);

        if (responseMessage.IsComplete)
        {
            channel.Complete();
            _responseChannels.TryRemove(topicId, out _);
            _cancellationTokens.TryRemove(topicId, out _);
            _streamBuffers.TryRemove(topicId, out _);
            _sequenceCounters.TryRemove(topicId, out _);
            _currentPrompts.TryRemove(topicId, out _);
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
            channel.Complete();
        }

        _streamBuffers.TryRemove(topicId, out _);
        _sequenceCounters.TryRemove(topicId, out _);
        _currentPrompts.TryRemove(topicId, out _);

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

        var broadcastChannel = new BroadcastChannel<ChatStreamMessage>();
        _responseChannels[topicId] = broadcastChannel;

        // Clear any stale buffer and sequence counter from previous request
        _streamBuffers.TryRemove(topicId, out _);
        _sequenceCounters.TryRemove(topicId, out _);

        // Store current prompt for reconnection
        _currentPrompts[topicId] = message;

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
        return broadcastChannel.Subscribe().ReadAllAsync(cts.Token);
    }

    public bool IsProcessing(string topicId)
    {
        return _responseChannels.ContainsKey(topicId);
    }

    public StreamState? GetStreamState(string topicId)
    {
        var isProcessing = _responseChannels.ContainsKey(topicId);
        _currentPrompts.TryGetValue(topicId, out var currentPrompt);

        if (!_streamBuffers.TryGetValue(topicId, out var buffer))
        {
            return isProcessing ? new StreamState(true, [], 0, currentPrompt) : null;
        }

        var messages = buffer.GetAll();
        var lastIndex = messages.LastOrDefault()?.MessageIndex ?? 0;

        return new StreamState(isProcessing, messages, lastIndex, currentPrompt);
    }

    public IAsyncEnumerable<ChatStreamMessage>? SubscribeToStream(string topicId, CancellationToken cancellationToken)
    {
        return !_responseChannels.TryGetValue(topicId, out var channel)
            ? null
            : channel.Subscribe().ReadAllAsync(cancellationToken);
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
            channel.Complete();
        }

        _streamBuffers.TryRemove(topicId, out _);
        _sequenceCounters.TryRemove(topicId, out _);
        _currentPrompts.TryRemove(topicId, out _);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _promptChannel.Writer.Complete();

        foreach (var channel in _responseChannels.Values)
        {
            channel.Complete();
        }

        foreach (var cts in _cancellationTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}

public record WebChatSession(string AgentId, long ChatId, long ThreadId);