using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IStreamingService
{
    Task StreamResponseAsync(StoredTopic topic, string message);


    Task ResumeStreamResponseAsync(StoredTopic topic, ChatMessageModel streamingMessage, string startMessageId);
}