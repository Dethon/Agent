using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Clients.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agent.Hubs;

public sealed class ChatHub(
    IAgentFactory agentFactory,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    IThreadStateStore threadStateStore,
    WebChatMessengerClient messengerClient,
    INotifier hubNotifier) : Hub
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

        // If not still processing, nothing to stream
        if (!state.IsProcessing)
        {
            yield break;
        }

        // Subscribe to live updates only - client already has buffer content
        var liveStream = messengerClient.SubscribeToStream(topicId, cancellationToken);
        if (liveStream is null)
        {
            yield break;
        }

        // Check for any pending approval directly (more reliable than buffer check)
        var pendingApproval = messengerClient.GetPendingApprovalForTopic(topicId);
        if (pendingApproval is not null)
        {
            yield return new ChatStreamMessage { ApprovalRequest = pendingApproval };
        }

        await foreach (var msg in liveStream.IgnoreCancellation(cancellationToken))
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

        await foreach (var msg in responses.IgnoreCancellation(ct: cancellationToken))
        {
            yield return msg;
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }
    }

    public async Task CancelTopic(string topicId)
    {
        messengerClient.CancelProcessing(topicId);
        await hubNotifier.NotifyStreamChangedAsync(
            new StreamChangedNotification(StreamChangeType.Cancelled, topicId));
    }

    public async Task DeleteTopic(string topicId, long chatId, long threadId)
    {
        messengerClient.EndSession(topicId);

        var agentKey = new AgentKey(chatId, threadId);
        await threadStateStore.DeleteAsync(agentKey);
        await threadStateStore.DeleteTopicAsync(topicId);

        await hubNotifier.NotifyTopicChangedAsync(
            new TopicChangedNotification(TopicChangeType.Deleted, topicId));
    }

    public async Task SaveTopic(TopicMetadata topic, bool isNew = false)
    {
        await threadStateStore.SaveTopicAsync(topic);

        var changeType = isNew ? TopicChangeType.Created : TopicChangeType.Updated;
        await hubNotifier.NotifyTopicChangedAsync(
            new TopicChangedNotification(changeType, topic.TopicId, topic));
    }

    public Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        return messengerClient.RespondToApprovalAsync(approvalId, result);
    }

    public bool IsApprovalPending(string approvalId)
    {
        return messengerClient.IsApprovalPending(approvalId);
    }

    public ToolApprovalRequestMessage? GetPendingApprovalForTopic(string topicId)
    {
        return messengerClient.GetPendingApprovalForTopic(topicId);
    }
}