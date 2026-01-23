using System.Collections.Concurrent;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatStreamManager(ILogger<WebChatStreamManager> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, BroadcastChannel<ChatStreamMessage>> _responseChannels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();
    private readonly ConcurrentDictionary<string, string> _currentPrompts = new();
    private readonly ConcurrentDictionary<string, string> _currentSenderIds = new();
    private readonly ConcurrentDictionary<string, int> _pendingPromptCounts = new();
    private readonly Lock _streamLock = new();
    private bool _disposed;

    public (BroadcastChannel<ChatStreamMessage> Channel, CancellationToken Token, bool IsNew) GetOrCreateStream(
        string topicId,
        string currentPrompt,
        string? currentSenderId,
        CancellationToken parentToken)
    {
        // If a stream already exists, return it (don't overwrite!)
        // This allows multiple browsers to share the same stream
        if (_responseChannels.TryGetValue(topicId, out var existingChannel))
        {
            // Update prompt info for the new message
            _currentPrompts[topicId] = currentPrompt;
            if (currentSenderId is not null)
            {
                _currentSenderIds[topicId] = currentSenderId;
            }

            // Get existing cancellation token
            if (_cancellationTokens.TryGetValue(topicId, out var existingCts))
            {
                return (existingChannel, existingCts.Token, IsNew: false);
            }

            // Fallback: create new linked token if somehow missing
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _cancellationTokens[topicId] = linkedCts;
            return (existingChannel, linkedCts.Token, IsNew: false);
        }

        // No existing stream - create new one
        var broadcastChannel = new BroadcastChannel<ChatStreamMessage>();
        _responseChannels[topicId] = broadcastChannel;

        _streamBuffers.TryRemove(topicId, out _);

        _currentPrompts[topicId] = currentPrompt;
        if (currentSenderId is not null)
        {
            _currentSenderIds[topicId] = currentSenderId;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _cancellationTokens[topicId] = cts;

        return (broadcastChannel, cts.Token, IsNew: true);
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

        var buffer = _streamBuffers.GetOrAdd(topicId, _ => new StreamBuffer());
        buffer.Add(message);
        await channel.WriteAsync(message, cancellationToken);
    }

    public void CompleteStream(string topicId)
    {
        lock (_streamLock)
        {
            if (_responseChannels.TryRemove(topicId, out var channel))
            {
                channel.Complete();
            }

            CleanupStreamState(topicId);
        }
    }

    public void CancelStream(string topicId)
    {
        lock (_streamLock)
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
    }

    public bool IsStreaming(string topicId)
    {
        return _responseChannels.ContainsKey(topicId);
    }

    public bool TryIncrementPending(string topicId)
    {
        lock (_streamLock)
        {
            if (!_responseChannels.ContainsKey(topicId))
            {
                return false;
            }

            _pendingPromptCounts.AddOrUpdate(topicId, 1, (_, count) => count + 1);
            return true;
        }
    }

    public bool DecrementPendingAndCheckIfShouldComplete(string topicId)
    {
        lock (_streamLock)
        {
            if (!_pendingPromptCounts.TryGetValue(topicId, out var count))
            {
                return false;
            }

            var newCount = count - 1;
            _pendingPromptCounts[topicId] = newCount;
            return newCount <= 0;
        }
    }

    public StreamState? GetStreamState(string topicId)
    {
        var isProcessing = _responseChannels.ContainsKey(topicId);
        _currentPrompts.TryGetValue(topicId, out var currentPrompt);
        _currentSenderIds.TryGetValue(topicId, out var currentSenderId);

        if (!_streamBuffers.TryGetValue(topicId, out var buffer))
        {
            return isProcessing ? new StreamState(true, [], string.Empty, currentPrompt, currentSenderId) : null;
        }

        var messages = buffer.GetAll();
        var lastMessageId = messages.LastOrDefault()?.MessageId ?? string.Empty;

        return new StreamState(isProcessing, messages, lastMessageId, currentPrompt, currentSenderId);
    }

    private void CleanupStreamState(string topicId)
    {
        _streamBuffers.TryRemove(topicId, out _);
        _currentPrompts.TryRemove(topicId, out _);
        _currentSenderIds.TryRemove(topicId, out _);
        _pendingPromptCounts.TryRemove(topicId, out _);

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