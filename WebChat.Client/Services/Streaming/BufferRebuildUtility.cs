using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Services.Streaming;

/// <summary>
/// Pure utility functions for rebuilding streaming state from buffered messages.
/// Used during stream reconnection to reconcile server buffer with client history.
/// </summary>
public static class BufferRebuildUtility
{
    /// <summary>
    /// Reconstructs chat messages from a server buffer, separating completed turns from in-progress streaming.
    /// </summary>
    /// <param name="bufferedMessages">Messages buffered on the server during disconnection</param>
    /// <param name="historyContent">Content already displayed to the user (to avoid duplicates)</param>
    /// <returns>Completed message turns and the current streaming message state</returns>
    public static (List<ChatMessageModel> CompletedTurns, ChatMessageModel StreamingMessage) RebuildFromBuffer(
        IReadOnlyList<ChatStreamMessage> bufferedMessages,
        HashSet<string> historyContent)
    {
        var currentMessage = new ChatMessageModel { Role = "assistant" };
        var completedTurns = new List<ChatMessageModel>();

        if (bufferedMessages.Count == 0)
        {
            return (completedTurns, currentMessage);
        }

        var turnGroups = bufferedMessages
            .GroupBy(m => m.MessageId)
            .OrderBy(g => g.Key)
            .ToList();

        var isFirstGroup = true;
        var needsReasoningSeparator = false;

        foreach (var turnGroup in turnGroups)
        {
            var chunks = turnGroup.ToList();
            var isComplete = chunks.Any(m => m.IsComplete);

            if (!isFirstGroup && !string.IsNullOrEmpty(currentMessage.Content))
            {
                var strippedMessage = StripKnownContent(currentMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage);
                }

                currentMessage = new ChatMessageModel { Role = "assistant" };
                needsReasoningSeparator = false;
            }
            else if (!isFirstGroup && !string.IsNullOrEmpty(currentMessage.Reasoning))
            {
                needsReasoningSeparator = true;
            }

            isFirstGroup = false;

            foreach (var chunk in chunks.Where(m => m is { IsComplete: false, Error: null }))
            {
                currentMessage = AccumulateChunk(currentMessage, chunk, ref needsReasoningSeparator);
            }

            if (isComplete)
            {
                var strippedMessage = StripKnownContent(currentMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage);
                }

                currentMessage = new ChatMessageModel { Role = "assistant" };
            }
        }

        var streamingMessage = StripKnownContent(currentMessage, historyContent);
        return (completedTurns, streamingMessage);
    }

    /// <summary>
    /// Removes content that already exists in history to prevent duplicate display.
    /// </summary>
    /// <param name="message">Message to process</param>
    /// <param name="historyContent">Content already displayed to the user</param>
    /// <returns>Message with known content removed</returns>
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

    /// <summary>
    /// Accumulates a stream chunk into the current message state.
    /// </summary>
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
