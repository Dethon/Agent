using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IStreamingService
{
    Task SendMessageAsync(StoredTopic topic, string message, string? correlationId = null);
    Task<bool> TryStartResumeStreamAsync(StoredTopic topic, ChatMessageModel streamingMessage, string startMessageId);
    Task<bool> IsStreamActiveAsync(string topicId);
}