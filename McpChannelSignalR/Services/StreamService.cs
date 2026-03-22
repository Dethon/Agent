using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Internal;
using Microsoft.Extensions.Logging;

namespace McpChannelSignalR.Services;

public sealed class StreamService(SessionService sessionService, ILogger<StreamService> logger) : IStreamService, IDisposable
{
    private readonly ConcurrentDictionary<string, BroadcastChannel<ChatStreamMessage>> _responseChannels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();
    private readonly ConcurrentDictionary<string, string> _currentPrompts = new();
    private readonly ConcurrentDictionary<string, string> _currentSenderIds = new();
    private readonly ConcurrentDictionary<string, int> _pendingPromptCounts = new();
    private readonly Lock _streamLock = new();
    private bool _disposed;

    public async Task WriteReplyAsync(SendReplyParams p)
    {
        var conversationId = p.ConversationId;
        var content = p.Content;
        var contentType = p.ContentType;
        var isComplete = p.IsComplete;
        var messageId = p.MessageId;

        // Resolve conversationId ("chatId:threadId") to topicId (client-generated UUID)
        var topicId = sessionService.GetTopicIdByConversationId(conversationId) ?? conversationId;

        // Use agent-provided messageId (from AgentResponseUpdate.MessageId) for proper bubble grouping
        var effectiveMessageId = messageId ?? topicId;

        var message = contentType switch
        {
            "text" => new ChatStreamMessage { Content = content, MessageId = effectiveMessageId },
            "reasoning" => new ChatStreamMessage { Reasoning = content, MessageId = effectiveMessageId },
            "tool_call" => new ChatStreamMessage { ToolCalls = FormatToolCall(content), MessageId = effectiveMessageId },
            "error" => new ChatStreamMessage { Error = content, IsComplete = true },
            "stream_complete" => new ChatStreamMessage { IsComplete = true, MessageId = effectiveMessageId },
            _ => new ChatStreamMessage { Content = content, MessageId = effectiveMessageId }
        };

        if (isComplete && contentType != "error" && contentType != "stream_complete")
        {
            await WriteMessageAsync(topicId, message);
            var completeMessage = new ChatStreamMessage { IsComplete = true, MessageId = effectiveMessageId };
            await WriteMessageAsync(topicId, completeMessage);
            CompleteStream(topicId);
            return;
        }

        if (contentType is "error" or "stream_complete")
        {
            await WriteMessageAsync(topicId, message);
            CompleteStream(topicId);
            return;
        }

        await WriteMessageAsync(topicId, message);
    }

    private static string FormatToolCall(string jsonContent)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;
            var toolName = root.GetProperty("Name").GetString()?.Split(':').LastOrDefault() ?? "unknown";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🔧 {toolName}");

            if (root.TryGetProperty("Arguments", out var args) && args.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in args.EnumerateObject())
                {
                    var val = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.GetRawText();
                    if (val.Length > 100) val = val[..100] + "...";
                    sb.AppendLine($"  {prop.Name}: {val}");
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return jsonContent;
        }
    }

    public (BroadcastChannel<ChatStreamMessage> Channel, CancellationToken Token, bool IsNew) GetOrCreateStream(
        string topicId,
        string currentPrompt,
        string? currentSenderId,
        CancellationToken parentToken)
    {
        if (_responseChannels.TryGetValue(topicId, out var existingChannel))
        {
            _currentPrompts[topicId] = currentPrompt;
            if (currentSenderId is not null)
            {
                _currentSenderIds[topicId] = currentSenderId;
            }

            if (_cancellationTokens.TryGetValue(topicId, out var existingCts))
            {
                return (existingChannel, existingCts.Token, IsNew: false);
            }

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _cancellationTokens[topicId] = linkedCts;
            return (existingChannel, linkedCts.Token, IsNew: false);
        }

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
        ChatStreamMessage message)
    {
        if (!_responseChannels.TryGetValue(topicId, out var channel))
        {
            logger.LogWarning("WriteMessage: topicId {TopicId} not found in _responseChannels", topicId);
            return;
        }

        var buffer = _streamBuffers.GetOrAdd(topicId, _ => new StreamBuffer());
        buffer.Add(message);
        await channel.WriteAsync(message, CancellationToken.None);
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
