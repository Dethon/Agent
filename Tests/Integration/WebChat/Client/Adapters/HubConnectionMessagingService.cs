using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace Tests.Integration.WebChat.Client.Adapters;

public sealed class HubConnectionMessagingService(HubConnection connection) : IChatMessagingService
{
    public async IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string topicId, string message,
        string? correlationId = null, AgentConfigPatch? configPatch = null)
    {
        await foreach (var msg in connection.StreamAsync<ChatStreamMessage>("SendMessage", topicId, message,
                           correlationId, configPatch))
        {
            yield return msg;
        }
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStreamAsync(string topicId)
    {
        await foreach (var msg in connection.StreamAsync<ChatStreamMessage>("ResumeStream", topicId))
        {
            yield return msg;
        }
    }

    public async Task<StreamState?> GetStreamStateAsync(string topicId)
    {
        return await connection.InvokeAsync<StreamState?>("GetStreamState", topicId);
    }

    public async Task CancelTopicAsync(string topicId)
    {
        await connection.InvokeAsync("CancelTopic", topicId);
    }

    public async Task<bool> EnqueueMessageAsync(
        string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null)
    {
        return await connection.InvokeAsync<bool>("EnqueueMessage", topicId, message, correlationId, configPatch);
    }
}