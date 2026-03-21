using System.Runtime.CompilerServices;
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
    ChannelNotificationEmitter notificationEmitter) : Hub
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

    public bool StartSession(string agentId, string topicId, long chatId, long threadId)
    {
        return sessionService.StartSession(topicId, agentId, chatId, threadId, CurrentSpaceSlug);
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
        var (broadcastChannel, linkedToken, _) =
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

    public Task DeleteTopic(string topicId)
    {
        sessionService.EndSession(topicId);
        streamService.CancelStream(topicId);
        approvalService.CancelPendingApprovalsForTopic(topicId);
        return Task.CompletedTask;
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
