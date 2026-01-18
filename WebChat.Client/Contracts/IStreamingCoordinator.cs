using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IStreamingCoordinator
{
    Task StreamResponseAsync(StoredTopic topic, string message, Func<Task> onRender);

    Task ResumeStreamResponseAsync(
        StoredTopic topic,
        ChatMessageModel streamingMessage,
        string startMessageId,
        Func<Task> onRender);

    (List<ChatMessageModel> CompletedTurns, ChatMessageModel StreamingMessage) RebuildFromBuffer(
        IReadOnlyList<ChatStreamMessage> bufferedMessages,
        HashSet<string> historyContent);

    ChatMessageModel StripKnownContent(ChatMessageModel message, HashSet<string> historyContent);
}