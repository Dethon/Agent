using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agent.Hubs;

public sealed class ChatHub(
    IAgentFactory agentFactory,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    IThreadStateStore threadStateStore,
    WebChatMessengerClient messengerClient,
    ChatThreadResolver threadResolver,
    INotifier hubNotifier,
    IConfiguration configuration) : Hub
{
    private bool IsRegistered => Context.Items.ContainsKey("UserId");

    private string? GetRegisteredUserId()
    {
        return Context.Items.TryGetValue("UserId", out var userId)
            ? userId as string
            : null;
    }

    public Task RegisterUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new HubException("User ID cannot be empty");
        }

        Context.Items["UserId"] = userId;
        return Task.CompletedTask;
    }

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
        var spaceSlug = Context.Items.TryGetValue("SpaceSlug", out var slug) ? slug as string : null;
        return ValidateAgent(agentId) && messengerClient.StartSession(topicId, agentId, chatId, threadId, spaceSlug);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistory(string agentId, long chatId, long threadId)
    {
        var agentKey = new AgentKey(chatId, threadId, agentId);
        var messages = await threadStateStore.GetMessagesAsync(agentKey.ToString());

        if (messages is null)
        {
            return [];
        }

        return messages
            .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
            .Select(m => new ChatHistoryMessage(
                m.MessageId,
                m.Role.Value,
                string.Join("", m.Contents.OfType<TextContent>().Select(c => c.Text)),
                m.GetSenderId(),
                m.GetTimestamp()))
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .ToList();
    }

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopics(string agentId, string spaceSlug = "default")
    {
        if (!IsValidSpace(spaceSlug))
        {
            return [];
        }

        // Leave previous space group if any
        if (Context.Items.TryGetValue("SpaceSlug", out var previous) && previous is string prevSlug && prevSlug != spaceSlug)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"space:{prevSlug}");
        }

        Context.Items["SpaceSlug"] = spaceSlug;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"space:{spaceSlug}");
        return await threadStateStore.GetAllTopicsAsync(agentId, spaceSlug);
    }

    public async Task<SpaceConfig?> JoinSpace(string spaceSlug)
    {
        var spaces = configuration.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
        var space = spaces.FirstOrDefault(s => s.Slug == spaceSlug);
        if (space is null)
        {
            return null;
        }

        // Leave previous space group if any
        if (Context.Items.TryGetValue("SpaceSlug", out var previous) && previous is string prevSlug && prevSlug != spaceSlug)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"space:{prevSlug}");
        }

        Context.Items["SpaceSlug"] = spaceSlug;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"space:{spaceSlug}");
        return space;
    }

    private bool IsValidSpace(string slug)
    {
        var spaces = configuration.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
        return spaces.Any(s => s.Slug == slug);
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
        string? correlationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsRegistered)
        {
            yield return new ChatStreamMessage
            {
                Error = "User not registered. Please call RegisterUser first.",
                IsComplete = true
            };
            yield break;
        }

        if (!messengerClient.TryGetSession(topicId, out _))
        {
            yield return new ChatStreamMessage
            {
                Error = "Session not found. Please start a session first.",
                IsComplete = true
            };
            yield break;
        }

        var userId = GetRegisteredUserId() ?? "Anonymous";
        var responses =
            messengerClient.EnqueuePromptAndGetResponses(topicId, message, userId, correlationId, cancellationToken);

        await foreach (var msg in responses.IgnoreCancellation(ct: cancellationToken))
        {
            yield return msg;
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }
    }

    public bool EnqueueMessage(string topicId, string message, string? correlationId)
    {
        if (!IsRegistered)
        {
            return false;
        }

        if (!messengerClient.TryGetSession(topicId, out _))
        {
            return false;
        }

        var userId = GetRegisteredUserId() ?? "Anonymous";
        return messengerClient.EnqueuePrompt(topicId, message, userId, correlationId);
    }

    public async Task CancelTopic(string topicId)
    {
        var spaceSlug = Context.Items.TryGetValue("SpaceSlug", out var slug) ? slug as string : null;
        messengerClient.CancelProcessing(topicId);
        await hubNotifier.NotifyStreamChangedAsync(
            new StreamChangedNotification(StreamChangeType.Cancelled, topicId, spaceSlug));
    }

    public async Task DeleteTopic(string agentId, string topicId, long chatId, long threadId)
    {
        var spaceSlug = Context.Items.TryGetValue("SpaceSlug", out var slug) ? slug as string : null;
        messengerClient.EndSession(topicId);

        var agentKey = new AgentKey(chatId, threadId, agentId);
        await threadStateStore.DeleteAsync(agentKey);
        await threadStateStore.DeleteTopicAsync(agentId, chatId, topicId);
        await threadResolver.ClearAsync(agentKey);

        await hubNotifier.NotifyTopicChangedAsync(
            new TopicChangedNotification(TopicChangeType.Deleted, topicId, SpaceSlug: spaceSlug));
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