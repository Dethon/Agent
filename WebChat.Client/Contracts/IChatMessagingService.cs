using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IChatMessagingService
{
    IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string topicId, string message);
    IAsyncEnumerable<ChatStreamMessage> ResumeStreamAsync(string topicId);
    Task<StreamState?> GetStreamStateAsync(string topicId);
    Task CancelTopicAsync(string topicId);
}