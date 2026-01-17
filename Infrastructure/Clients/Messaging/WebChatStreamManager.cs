using System.Collections.Concurrent;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatStreamManager(ILogger<WebChatStreamManager> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, BroadcastChannel<ChatStreamMessage>> _responseChannels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();
    private readonly ConcurrentDictionary<string, long> _sequenceCounters = new();
    private readonly ConcurrentDictionary<string, string> _currentPrompts = new();
    private bool _disposed;

    public (BroadcastChannel<ChatStreamMessage> Channel, CancellationToken Token) CreateStream(
        string topicId,
        string currentPrompt,
        CancellationToken parentToken)
    {
        var broadcastChannel = new BroadcastChannel<ChatStreamMessage>();
        _responseChannels[topicId] = broadcastChannel;

        _streamBuffers.TryRemove(topicId, out _);
        _sequenceCounters.TryRemove(topicId, out _);

        _currentPrompts[topicId] = currentPrompt;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _cancellationTokens[topicId] = cts;

        return (broadcastChannel, cts.Token);
    }

    public IAsyncEnumerable<ChatStreamMessage>? SubscribeToStream(
        string topicId,
        CancellationToken cancellationToken)
    {
        return !_responseChannels.TryGetValue(topicId, out var channel)
            ? null
            : channel.Subscribe().ReadAllAsync(cancellationToken);
    }

    public async Task WriteMessageAsync(
        string topicId,
        ChatStreamMessage message,
        CancellationToken cancellationToken)
    {
        if (!_responseChannels.TryGetValue(topicId, out var channel))
        {
            logger.LogWarning("WriteMessage: topicId {TopicId} not found in _responseChannels", topicId);
            return;
        }

        var sequenceNumber = _sequenceCounters.AddOrUpdate(topicId, 1, (_, seq) => seq + 1);
        var messageWithSequence = message with { SequenceNumber = sequenceNumber };

        var buffer = _streamBuffers.GetOrAdd(topicId, _ => new StreamBuffer());
        buffer.Add(messageWithSequence);
        await channel.WriteAsync(messageWithSequence, cancellationToken);
    }

    public void CompleteStream(string topicId)
    {
        if (_responseChannels.TryRemove(topicId, out var channel))
        {
            channel.Complete();
        }

        // Only clean up the CTS, keep buffer for stream resume
        if (_cancellationTokens.TryRemove(topicId, out var cts))
        {
            cts.Dispose();
        }
    }

    public void CancelStream(string topicId)
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

        CleanupStreamState(topicId);
    }

    public bool IsStreaming(string topicId)
    {
        return _responseChannels.ContainsKey(topicId);
    }

    public StreamState? GetStreamState(string topicId)
    {
        var isProcessing = _responseChannels.ContainsKey(topicId);
        _currentPrompts.TryGetValue(topicId, out var currentPrompt);

        if (!_streamBuffers.TryGetValue(topicId, out var buffer))
        {
            return isProcessing ? new StreamState(true, [], string.Empty, currentPrompt) : null;
        }

        var messages = buffer.GetAll();
        var lastMessageId = messages.LastOrDefault()?.MessageId ?? string.Empty;

        return new StreamState(isProcessing, messages, lastMessageId, currentPrompt);
    }

    private void CleanupStreamState(string topicId)
    {
        _streamBuffers.TryRemove(topicId, out _);
        _sequenceCounters.TryRemove(topicId, out _);
        _currentPrompts.TryRemove(topicId, out _);

        if (_cancellationTokens.TryRemove(topicId, out var cts))
        {
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

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