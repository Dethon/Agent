using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IStreamingService
{
    Task SendMessageAsync(StoredTopic topic, string message);
    Task StreamResponseAsync(StoredTopic topic, string message);
    Task ResumeStreamResponseAsync(StoredTopic topic, ChatMessageModel streamingMessage, string startMessageId);
}