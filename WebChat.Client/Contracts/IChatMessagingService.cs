using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IChatMessagingService
{
    IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string topicId, string message, string? senderId);
    IAsyncEnumerable<ChatStreamMessage> ResumeStreamAsync(string topicId);
    Task<StreamState?> GetStreamStateAsync(string topicId);
    Task CancelTopicAsync(string topicId);
}