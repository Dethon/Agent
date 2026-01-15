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

    public bool StartSession(string agentId, string topicId, long chatId)
    {
        return ValidateAgent(agentId) && messengerClient.StartSession(topicId, agentId, chatId);
    }

    public IReadOnlyList<ChatHistoryMessage> GetHistory(long chatId)
    {
        var agentKey = new AgentKey(chatId, 0);
        var messages = threadStateStore.GetMessages(agentKey.ToString());

        if (messages is null)
        {
            return [];
        }

        return messages
            .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            .Select(m => new ChatHistoryMessage(
                m.Role.Value,
                string.Join("", m.Contents.OfType<TextContent>().Select(c => c.Text))))
            .ToList();
    }

    public bool IsProcessing(string topicId)
    {
        return messengerClient.IsProcessing(topicId);
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

    public async Task DeleteTopic(string topicId, long chatId)
    {
        messengerClient.EndSession(topicId);

        var agentKey = new AgentKey(chatId, 0);
        await threadStateStore.DeleteAsync(agentKey);
    }
}