using System.Text;
using Domain.DTOs.WebChat;

namespace Infrastructure.Clients.Messaging;

internal sealed class StreamBuffer
{
    private readonly List<ChatStreamMessage> _messages = [];
    private readonly Dictionary<string, int> _messageIndexByMessageId = new();
    private readonly Dictionary<string, StringBuilder> _contentByMessageId = new();
    private readonly Dictionary<string, StringBuilder> _reasoningByMessageId = new();
    private readonly Dictionary<string, StringBuilder> _toolCallsByMessageId = new();
    private readonly Lock _lock = new();
    private const int MaxBufferSize = 100;

    public void Add(ChatStreamMessage message)
    {
        lock (_lock)
        {
            // Special messages (user message, approval, complete, error) are stored as-is
            if (message.UserMessage is not null ||
                message.ApprovalRequest is not null ||
                message.IsComplete ||
                message.Error is not null)
            {
                AddNewMessage(message);
                return;
            }

            // Content chunks are consolidated by MessageId
            if (message.MessageId is null)
            {
                AddNewMessage(message);
                return;
            }

            if (_messageIndexByMessageId.TryGetValue(message.MessageId, out var index))
            {
                // Append to existing message
                AppendToMessage(message, index);
            }
            else
            {
                // New message - add entry and track it
                AddNewMessage(message);
                _messageIndexByMessageId[message.MessageId] = _messages.Count - 1;

                if (message.Content is not null)
                {
                    _contentByMessageId[message.MessageId] = new StringBuilder(message.Content);
                }

                if (message.Reasoning is not null)
                {
                    _reasoningByMessageId[message.MessageId] = new StringBuilder(message.Reasoning);
                }

                if (message.ToolCalls is not null)
                {
                    _toolCallsByMessageId[message.MessageId] = new StringBuilder(message.ToolCalls);
                }
            }
        }
    }

    private void AddNewMessage(ChatStreamMessage message)
    {
        if (_messages.Count >= MaxBufferSize)
        {
            RemoveOldestMessage();
        }

        _messages.Add(message);
    }

    private void RemoveOldestMessage()
    {
        var oldest = _messages[0];
        _messages.RemoveAt(0);

        // Clean up tracking for removed message
        if (oldest.MessageId is not null && _messageIndexByMessageId.ContainsKey(oldest.MessageId))
        {
            _messageIndexByMessageId.Remove(oldest.MessageId);
            _contentByMessageId.Remove(oldest.MessageId);
            _reasoningByMessageId.Remove(oldest.MessageId);
            _toolCallsByMessageId.Remove(oldest.MessageId);
        }

        // Adjust indices for remaining tracked messages
        foreach (var key in _messageIndexByMessageId.Keys.ToList())
        {
            _messageIndexByMessageId[key]--;
        }
    }

    private void AppendToMessage(ChatStreamMessage chunk, int index)
    {
        var messageId = chunk.MessageId!;

        if (chunk.Content is not null)
        {
            if (!_contentByMessageId.TryGetValue(messageId, out var sb))
            {
                sb = new StringBuilder();
                _contentByMessageId[messageId] = sb;
            }

            sb.Append(chunk.Content);
        }

        if (chunk.Reasoning is not null)
        {
            if (!_reasoningByMessageId.TryGetValue(messageId, out var sb))
            {
                sb = new StringBuilder();
                _reasoningByMessageId[messageId] = sb;
            }

            sb.Append(chunk.Reasoning);
        }

        if (chunk.ToolCalls is not null)
        {
            if (!_toolCallsByMessageId.TryGetValue(messageId, out var sb))
            {
                sb = new StringBuilder();
                _toolCallsByMessageId[messageId] = sb;
            }

            sb.Append(chunk.ToolCalls);
        }

        // Update the message in the list with accumulated content
        _messages[index] = _messages[index] with
        {
            Content = _contentByMessageId.GetValueOrDefault(messageId)?.ToString(),
            Reasoning = _reasoningByMessageId.GetValueOrDefault(messageId)?.ToString(),
            ToolCalls = _toolCallsByMessageId.GetValueOrDefault(messageId)?.ToString(),
            SequenceNumber = chunk.SequenceNumber
        };
    }

    public IReadOnlyList<ChatStreamMessage> GetAll()
    {
        lock (_lock)
        {
            return [.. _messages];
        }
    }
}