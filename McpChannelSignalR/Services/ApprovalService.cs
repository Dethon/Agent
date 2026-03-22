using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

public sealed class ApprovalService(
    StreamService streamService,
    SessionService sessionService,
    IHubNotificationSender hubNotificationSender,
    ILogger<ApprovalService> logger) : IApprovalService
{
    private readonly ConcurrentDictionary<string, ApprovalContext> _pendingApprovals = new();

    public async Task<string> RequestApprovalAsync(RequestApprovalParams p)
    {
        var topicId = sessionService.GetTopicIdByConversationId(p.ConversationId) ?? p.ConversationId;
        var requests = DeserializeRequests(p.Requests);
        var approvalId = Guid.NewGuid().ToString("N")[..8];

        var context = new ApprovalContext
        {
            TopicId = topicId,
            Requests = requests
        };

        _pendingApprovals[approvalId] = context;

        try
        {
            var approvalMessage = new ChatStreamMessage
            {
                ApprovalRequest = new ToolApprovalRequestMessage(approvalId, requests)
            };

            await streamService.WriteMessageAsync(topicId, approvalMessage);

            var result = await context.WaitForApprovalAsync(CancellationToken.None);

            if (result is not (ToolApprovalResult.Approved or ToolApprovalResult.ApprovedAndRemember))
            {
                return result.ToString().ToLowerInvariant();
            }

            var toolCallsMessage = new ChatStreamMessage
            {
                ToolCalls = FormatToolCalls(requests)
            };

            await streamService.WriteMessageAsync(topicId, toolCallsMessage);

            return result.ToString().ToLowerInvariant();
        }
        finally
        {
            _pendingApprovals.TryRemove(approvalId, out _);
            context.Dispose();
        }
    }

    public async Task NotifyAutoApprovedAsync(RequestApprovalParams p)
    {
        var topicId = sessionService.GetTopicIdByConversationId(p.ConversationId) ?? p.ConversationId;
        var requests = DeserializeRequests(p.Requests);

        var messages = requests
            .GroupBy(x => x.MessageId)
            .Select(g => new ChatStreamMessage
            {
                MessageId = g.Key,
                ToolCalls = FormatToolCalls(g.ToArray())
            });

        sessionService.TryGetSession(topicId, out var session);

        foreach (var message in messages)
        {
            await streamService.WriteMessageAsync(topicId, message);

            try
            {
                var notification = new ToolCallsNotification(
                    p.ConversationId, message.ToolCalls!, message.MessageId, SpaceSlug: session?.SpaceSlug);
                await SendToSpaceOrAllAsync(session?.SpaceSlug, "OnToolCalls", notification);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to notify tool calls for topic {TopicId}", p.ConversationId);
            }
        }
    }

    public async Task RespondToApprovalAsync(string approvalId, string result)
    {
        if (!_pendingApprovals.TryRemove(approvalId, out var context))
        {
            logger.LogWarning("RespondToApproval: approvalId {ApprovalId} not found or already processed", approvalId);
            return;
        }

        var approvalResult = Enum.Parse<ToolApprovalResult>(result, ignoreCase: true);

        var toolCalls = approvalResult is ToolApprovalResult.Approved or ToolApprovalResult.ApprovedAndRemember
            ? FormatToolCalls(context.Requests)
            : null;

        var messageId = context.Requests.FirstOrDefault()?.MessageId;

        sessionService.TryGetSession(context.TopicId, out var session);

        try
        {
            var notification = new ApprovalResolvedNotification(
                context.TopicId, approvalId, toolCalls, messageId, SpaceSlug: session?.SpaceSlug);
            await SendToSpaceOrAllAsync(session?.SpaceSlug, "OnApprovalResolved", notification);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify approval resolved for topic {TopicId}", context.TopicId);
        }

        context.TrySetResult(approvalResult);
        context.Dispose();
    }

    public bool IsApprovalPending(string approvalId)
    {
        return _pendingApprovals.ContainsKey(approvalId);
    }

    public ToolApprovalRequestMessage? GetPendingApprovalForTopic(string topicId)
    {
        var pending = _pendingApprovals
            .FirstOrDefault(kv => kv.Value.TopicId == topicId);

        return pending.Key is null
            ? null
            : new ToolApprovalRequestMessage(pending.Key, pending.Value.Requests);
    }

    public void CancelPendingApprovalsForTopic(string topicId)
    {
        var expiredApprovals = _pendingApprovals
            .Where(kv => kv.Value.TopicId == topicId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var approvalId in expiredApprovals)
        {
            if (!_pendingApprovals.TryRemove(approvalId, out var context))
            {
                continue;
            }

            context.TrySetResult(ToolApprovalResult.Rejected);
            context.Dispose();
        }
    }

    private async Task SendToSpaceOrAllAsync(string? spaceSlug, string methodName, object notification)
    {
        if (spaceSlug is not null)
        {
            await hubNotificationSender.SendToGroupAsync($"space:{spaceSlug}", methodName, notification);
        }
        else
        {
            await hubNotificationSender.SendAsync(methodName, notification);
        }
    }

    private static IReadOnlyList<ToolApprovalRequest> DeserializeRequests(string requestsJson)
    {
        return JsonSerializer.Deserialize<List<ToolApprovalRequest>>(requestsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string FormatToolCalls(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();

        foreach (var request in requests)
        {
            var toolName = request.ToolName.Split(':').Last();
            sb.AppendLine($"🔧 {toolName}");

            if (request.Arguments.Count <= 0)
            {
                continue;
            }

            foreach (var (key, value) in request.Arguments)
            {
                var formattedValue = FormatArgumentValue(value);
                if (formattedValue.Length > 100)
                {
                    formattedValue = formattedValue[..100] + "...";
                }

                sb.AppendLine($"  {key}: {formattedValue}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatArgumentValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => s.Replace("\n", " ").Replace("\r", ""),
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString()?.Replace("\n", " ") ?? "",
            JsonElement je => je.GetRawText(),
            _ => value.ToString() ?? ""
        };
    }
}
