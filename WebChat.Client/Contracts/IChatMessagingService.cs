using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IChatMessagingService
{
    // The streaming calls answer before iteration rather than by iterating nothing, so a
    // caller learns a stream will not start before it announces one.
    Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> SendMessageAsync(
        string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null);
    Task<HubResult<IAsyncEnumerable<ChatStreamMessage>>> ResumeStreamAsync(string topicId);
    Task<HubResult<StreamState>> GetStreamStateAsync(string topicId);
    Task CancelTopicAsync(string topicId);
    Task<HubResult<bool>> EnqueueMessageAsync(
        string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null);
}