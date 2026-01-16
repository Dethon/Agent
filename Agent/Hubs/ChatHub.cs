using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agent.Hubs;

public sealed class ChatHub(
    IAgentFactory agentFactory,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    IThreadStateStore threadStateStore,
    WebChatMessengerClient messengerClient) : Hub
{
    public IReadOnlyList<AgentInfo> GetAgents()
    {
        return agentFactory.GetAvailableAgents();
    }

    public bool ValidateAgent(string agentId)
    {
        var agents = registryOptions.CurrentValue.Agents;
        return agents.Any(a => a.Id == agentId);
    }

    public bool StartSession(string agentId, string topicId, long chatId, long threadId)
    {
        return ValidateAgent(agentId) && messengerClient.StartSession(topicId, agentId, chatId, threadId);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistory(long chatId, long threadId)
    {
        var agentKey = new AgentKey(chatId, threadId);
        var messages = await threadStateStore.GetMessagesAsync(agentKey.ToString());

        if (messages is null)
        {
            return [];
        }

        return messages
            .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            .Select(m => new ChatHistoryMessage(
                m.Role.Value,
                string.Join("", m.Contents.OfType<TextContent>().Select(c => c.Text))))
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .ToList();
    }

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopics()
    {
        return await threadStateStore.GetAllTopicsAsync();
    }

    public bool IsProcessing(string topicId)
    {
        return messengerClient.IsProcessing(topicId);
    }

    public StreamState? GetStreamState(string topicId)
    {
        return messengerClient.GetStreamState(topicId);
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStream(
        string topicId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = messengerClient.GetStreamState(topicId);
        if (state is null)
        {
            yield break;
        }

        // Replay buffered messages first (client will deduplicate by sequence number)
        foreach (var msg in state.BufferedMessages)
        {
            yield return msg;
        }

        // If not still processing, we're done
        if (!state.IsProcessing)
        {
            yield break;
        }

        // Subscribe to live updates
        var liveStream = messengerClient.SubscribeToStream(topicId, cancellationToken);
        if (liveStream is null)
        {
            yield break;
        }

        await foreach (var msg in liveStream)
        {
            yield return msg;
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }
    }

    public async IAsyncEnumerable<ChatStreamMessage> SendMessage(
        string topicId,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!messengerClient.TryGetSession(topicId, out _))
        {
            yield return new ChatStreamMessage
            {
                Error = "Session not found. Please start a session first.",
                IsComplete = true
            };
            yield break;
        }

        var responses = messengerClient.EnqueuePromptAndGetResponses(topicId, message, "web-user", cancellationToken);

        await foreach (var msg in responses)
        {
            yield return msg;
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }
    }

    public Task CancelTopic(string topicId)
    {
        messengerClient.CancelProcessing(topicId);
        return Task.CompletedTask;
    }

    public async Task DeleteTopic(string topicId, long chatId, long threadId)
    {
        messengerClient.EndSession(topicId);

        var agentKey = new AgentKey(chatId, threadId);
        await threadStateStore.DeleteAsync(agentKey);
        await threadStateStore.DeleteTopicAsync(topicId);
    }

    public async Task SaveTopic(TopicMetadata topic)
    {
        await threadStateStore.SaveTopicAsync(topic);
    }
}