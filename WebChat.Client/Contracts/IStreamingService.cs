using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IStreamingService
{
    Task SendMessageAsync(StoredTopic topic, string message, string? correlationId = null);
    Task StreamResponseAsync(StoredTopic topic, string message, string? correlationId = null);
    Task ResumeStreamResponseAsync(StoredTopic topic, ChatMessageModel streamingMessage, string startMessageId);
}