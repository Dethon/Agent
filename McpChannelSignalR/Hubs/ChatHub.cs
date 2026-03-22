using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using McpChannelSignalR.Services;
using Microsoft.AspNetCore.SignalR;

namespace McpChannelSignalR.Hubs;

public sealed class ChatHub(
    SessionService sessionService,
    StreamService streamService,
    ApprovalService approvalService,
    ChannelNotificationEmitter notificationEmitter,
    AgentApiClient agentApiClient,
    RedisStateService redisStateService,
    IPushSubscriptionStore pushSubscriptionStore) : Hub
{
    private bool IsRegistered => Context.Items.ContainsKey("UserId");

    private string? CurrentSpaceSlug =>
        Context.Items.TryGetValue("SpaceSlug", out var slug) ? slug as string : null;

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

    public Task<IReadOnlyList<AgentInfo>> GetAgents()
    {
        return agentApiClient.GetAgentsAsync(GetRegisteredUserId());
    }

    public Task<AgentInfo> RegisterCustomAgent(CustomAgentRegistration registration)
    {
        var userId = GetRegisteredUserId() ?? throw new HubException("User not registered");
        return agentApiClient.RegisterCustomAgentAsync(userId, registration);
    }

    public Task<bool> UnregisterCustomAgent(string agentId)
    {
        var userId = GetRegisteredUserId() ?? throw new HubException("User not registered");
        return agentApiClient.UnregisterCustomAgentAsync(userId, agentId);
    }

    public async Task<bool> ValidateAgent(string agentId)
    {
        var agents = await agentApiClient.GetAgentsAsync(GetRegisteredUserId());
        return agents.Any(a => a.Id == agentId);
    }

    public async Task<bool> StartSession(string agentId, string topicId, long chatId, long threadId)
    {
        return await ValidateAgent(agentId)
            && sessionService.StartSession(topicId, agentId, chatId, threadId, CurrentSpaceSlug);
    }

    public async Task JoinSpace(string spaceSlug)
    {
        if (!SpaceConfig.IsValidSlug(spaceSlug))
        {
            throw new HubException("Invalid space slug");
        }

        await SwitchSpaceGroupAsync(spaceSlug);
    }

    private async Task SwitchSpaceGroupAsync(string spaceSlug)
    {
        if (Context.Items.TryGetValue("SpaceSlug", out var previous) && previous is string prevSlug)
        {
            if (prevSlug == spaceSlug)
            {
                return;
            }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"space:{prevSlug}");
        }

        Context.Items["SpaceSlug"] = spaceSlug;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"space:{spaceSlug}");
    }

    public bool IsProcessing(string topicId)
    {
        return streamService.IsStreaming(topicId);
    }

    public StreamState? GetStreamState(string topicId)
    {
        return streamService.GetStreamState(topicId);
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStream(
        string topicId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = streamService.GetStreamState(topicId);
        if (state is null || !state.IsProcessing)
        {
            yield break;
        }

        var liveStream = streamService.SubscribeToStream(topicId, cancellationToken);
        if (liveStream is null)
        {
            yield break;
        }

        var pendingApproval = approvalService.GetPendingApprovalForTopic(topicId);
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

        if (!sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            yield return new ChatStreamMessage
            {
                Error = "Session not found. Please start a session first.",
                IsComplete = true
            };
            yield break;
        }

        var userId = GetRegisteredUserId() ?? "Anonymous";

        // Create or join stream for this topic
        var (broadcastChannel, linkedToken) = 
            streamService.GetOrCreateStream(topicId, message, userId, cancellationToken);
        streamService.TryIncrementPending(topicId);

        // Write user message to buffer for other browsers
        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(userId, timestamp)
        };
        await streamService.WriteMessageAsync(topicId, userMessage);

        // Emit MCP notification to agent so it processes the message
        await notificationEmitter.EmitMessageNotificationAsync(
            $"{session.ChatId}:{session.ThreadId}",
            userId,
            message,
            session.AgentId,
            cancellationToken);

        // Subscribe and stream responses back to the browser
        await foreach (var msg in broadcastChannel.Subscribe().ReadAllAsync(linkedToken).IgnoreCancellation(cancellationToken))
        {
            yield return msg;
            if (msg.IsComplete || msg.Error is not null)
            {
                break;
            }
        }
    }

    public async Task<bool> EnqueueMessage(string topicId, string message, string? correlationId)
    {
        if (!IsRegistered)
        {
            return false;
        }

        if (!sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            return false;
        }

        if (!streamService.TryIncrementPending(topicId))
        {
            return false;
        }

        var userId = GetRegisteredUserId() ?? "Anonymous";

        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(userId, timestamp)
        };
        await streamService.WriteMessageAsync(topicId, userMessage);

        await notificationEmitter.EmitMessageNotificationAsync(
            $"{session.ChatId}:{session.ThreadId}",
            userId,
            message,
            session.AgentId);

        return true;
    }

    public Task CancelTopic(string topicId)
    {
        streamService.CancelStream(topicId);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopics(string agentId, string spaceSlug = "default")
    {
        return await redisStateService.GetAllTopicsAsync(agentId, spaceSlug);
    }

    public async Task SaveTopic(TopicMetadata topic, bool isNew = false)
    {
        await redisStateService.SaveTopicAsync(topic);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistory(string agentId, long chatId, long threadId)
    {
        return await redisStateService.GetHistoryAsync(agentId, chatId, threadId);
    }

    public async Task DeleteTopic(string agentId, string topicId, long chatId, long threadId)
    {
        sessionService.EndSession(topicId);
        streamService.CancelStream(topicId);
        approvalService.CancelPendingApprovalsForTopic(topicId);

        var agentKey = new AgentKey($"{chatId}:{threadId}", agentId);
        await redisStateService.DeleteMessagesAsync(agentKey);
        await redisStateService.DeleteTopicAsync(agentId, chatId, topicId);
    }

    public async Task SubscribePush(PushSubscriptionDto subscription)
    {
        var userId = GetRegisteredUserId()
            ?? throw new HubException("User not registered. Call RegisterUser first.");

        ValidateSubscription(subscription);

        await pushSubscriptionStore.SaveAsync(userId, subscription, CurrentSpaceSlug ?? "default");
    }

    public async Task ReplacePushSubscription(PushSubscriptionDto subscription, string oldEndpoint)
    {
        var userId = GetRegisteredUserId()
            ?? throw new HubException("User not registered. Call RegisterUser first.");

        ValidateSubscription(subscription);

        if (string.IsNullOrWhiteSpace(oldEndpoint))
        {
            throw new HubException("Old endpoint is required for replacement.");
        }

        await pushSubscriptionStore.SaveAsync(userId, subscription, CurrentSpaceSlug ?? "default",
            replacingEndpoint: oldEndpoint);
    }

    public async Task UnsubscribePush(string endpoint)
    {
        var userId = GetRegisteredUserId()
            ?? throw new HubException("User not registered. Call RegisterUser first.");
        await pushSubscriptionStore.RemoveAsync(userId, endpoint);
    }

    private static void ValidateSubscription(PushSubscriptionDto subscription)
    {
        if (string.IsNullOrWhiteSpace(subscription.Endpoint)
            || !Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https")
        {
            throw new HubException("Endpoint must be a valid HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(subscription.P256dh) || string.IsNullOrWhiteSpace(subscription.Auth))
        {
            throw new HubException("P256dh and Auth keys are required.");
        }
    }

    public Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        return Task.FromResult(RespondToApproval(approvalId, result));
    }

    private bool RespondToApproval(string approvalId, ToolApprovalResult result)
    {
        // Fire and forget - the approval service will resolve the TCS
        _ = approvalService.RespondToApprovalAsync(approvalId, result.ToString());
        return true;
    }

    public bool IsApprovalPending(string approvalId)
    {
        return approvalService.IsApprovalPending(approvalId);
    }

    public ToolApprovalRequestMessage? GetPendingApprovalForTopic(string topicId)
    {
        return approvalService.GetPendingApprovalForTopic(topicId);
    }
}
