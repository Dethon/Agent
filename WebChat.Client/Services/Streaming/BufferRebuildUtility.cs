using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Services.Streaming;

public static class BufferRebuildUtility
{
    public static (List<ChatMessageModel> CompletedTurns, ChatMessageModel StreamingMessage) RebuildFromBuffer(
        IReadOnlyList<ChatStreamMessage> bufferedMessages,
        HashSet<string> historyContent)
    {
        var completedTurns = new List<ChatMessageModel>();
        var currentAssistantMessage = new ChatMessageModel { Role = "assistant" };

        if (bufferedMessages.Count == 0)
        {
            return (completedTurns, currentAssistantMessage);
        }

        // Process messages in sequence order
        var orderedMessages = bufferedMessages
            .OrderBy(m => m.SequenceNumber)
            .ToList();

        var needsReasoningSeparator = false;
        string? currentMessageId = null;

        foreach (var msg in orderedMessages)
        {
            // Handle user messages - they're always complete, add directly
            if (msg.UserMessage is not null)
            {
                // If we have pending assistant content, save it first
                if (currentAssistantMessage.HasContent)
                {
                    var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
                    if (strippedMessage.HasContent)
                    {
                        completedTurns.Add(strippedMessage);
                    }
                    currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
                    needsReasoningSeparator = false;
                }

                completedTurns.Add(new ChatMessageModel
                {
                    Role = "user",
                    Content = msg.Content ?? "",
                    SenderId = msg.UserMessage.SenderId
                });
                continue;
            }

            // Handle assistant messages
            // If message ID changed and we have content, save the previous turn
            if (currentMessageId is not null && msg.MessageId != currentMessageId && currentAssistantMessage.HasContent)
            {
                var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage);
                }
                currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
                needsReasoningSeparator = false;
            }

            currentMessageId = msg.MessageId;

            // Skip complete markers and errors for accumulation
            if (msg.IsComplete || msg.Error is not null)
            {
                if (msg.IsComplete && currentAssistantMessage.HasContent)
                {
                    var strippedMessage = StripKnownContent(currentAssistantMessage, historyContent);
                    if (strippedMessage.HasContent)
                    {
                        completedTurns.Add(strippedMessage);
                    }
                    currentAssistantMessage = new ChatMessageModel { Role = "assistant" };
                    needsReasoningSeparator = false;
                }
                continue;
            }

            currentAssistantMessage = AccumulateChunk(currentAssistantMessage, msg, ref needsReasoningSeparator);
        }

        var streamingMessage = StripKnownContent(currentAssistantMessage, historyContent);
        return (completedTurns, streamingMessage);
    }


    public static ChatMessageModel StripKnownContent(ChatMessageModel message, HashSet<string> historyContent)
    {
        if (string.IsNullOrEmpty(message.Content))
        {
            return message;
        }

        // If the buffer content is a subset of any history content, it's a duplicate
        // (user disconnected mid-stream and buffer has incomplete content while history has complete)
        if (historyContent.Any(known => known.Contains(message.Content)))
        {
            return message with { Content = "" };
        }

        // Remove any history content that appears as a prefix in this message
        // (buffer has more content than was in history)
        var content = message.Content;
        foreach (var known in historyContent.Where(known => content.StartsWith(known)))
        {
            content = content[known.Length..].TrimStart();
        }

        return content != message.Content ? message with { Content = content } : message;
    }

    internal static ChatMessageModel AccumulateChunk(
        ChatMessageModel streamingMessage,
        ChatStreamMessage chunk,
        ref bool needsReasoningSeparator)
    {
        if (!string.IsNullOrEmpty(chunk.Content))
        {
            streamingMessage = streamingMessage with
            {
                Content = string.IsNullOrEmpty(streamingMessage.Content)
                    ? chunk.Content
                    : streamingMessage.Content + chunk.Content
            };
        }

        if (!string.IsNullOrEmpty(chunk.Reasoning))
        {
            var separator = needsReasoningSeparator ? "\n-----\n" : "";
            needsReasoningSeparator = false;
            streamingMessage = streamingMessage with
            {
                Reasoning = string.IsNullOrEmpty(streamingMessage.Reasoning)
                    ? chunk.Reasoning
                    : streamingMessage.Reasoning + separator + chunk.Reasoning
            };
        }

        if (!string.IsNullOrEmpty(chunk.ToolCalls))
        {
            streamingMessage = streamingMessage with
            {
                ToolCalls = string.IsNullOrEmpty(streamingMessage.ToolCalls)
                    ? chunk.ToolCalls
                    : streamingMessage.ToolCalls + "\n" + chunk.ToolCalls
            };
        }

        return streamingMessage;
    }
}