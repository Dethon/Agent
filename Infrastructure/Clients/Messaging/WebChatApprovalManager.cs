using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Extensions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatApprovalManager(
    WebChatStreamManager streamManager,
    INotifier notifier,
    ILogger<WebChatApprovalManager> logger)
{
    private readonly ConcurrentDictionary<string, ApprovalContext> _pendingApprovals = new();

    public async Task<ToolApprovalResult> RequestApprovalAsync(
        string topicId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
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

            await streamManager.WriteMessageAsync(topicId, approvalMessage, cancellationToken);

            var result = await context.WaitForApprovalAsync(cancellationToken);

            if (result is not (ToolApprovalResult.Approved or ToolApprovalResult.ApprovedAndRemember))
            {
                return result;
            }

            var toolCallsMessage = new ChatStreamMessage
            {
                ToolCalls = FormatToolCalls(requests)
            };

            await streamManager.WriteMessageAsync(topicId, toolCallsMessage, cancellationToken);

            return result;
        }
        finally
        {
            _pendingApprovals.TryRemove(approvalId, out _);
            context.Dispose();
        }
    }

    public async Task NotifyAutoApprovedAsync(
        string topicId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var messages = requests
            .GroupBy(x => x.MessageId)
            .Select(g => new ChatStreamMessage
            {
                MessageId = g.Key,
                ToolCalls = FormatToolCalls(g.ToArray())
            });

        foreach (var message in messages)
        {
            await streamManager.WriteMessageAsync(topicId, message, cancellationToken);
            // Also broadcast as notification to ensure all browsers receive it
            // (handles race condition where browser subscribes after this message is sent)
            await notifier.NotifyToolCallsAsync(
                    new ToolCallsNotification(topicId, message.ToolCalls!, message.MessageId), cancellationToken)
                .SafeAwaitAsync(logger, "Failed to notify tool calls for topic {TopicId}", topicId);
        }
    }

    public async Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        if (!_pendingApprovals.TryRemove(approvalId, out var context))
        {
            logger.LogWarning("RespondToApproval: approvalId {ApprovalId} not found or already processed", approvalId);
            return false;
        }

        // Include formatted tool calls in notification if approved, so all browsers can display them
        var toolCalls = result is ToolApprovalResult.Approved or ToolApprovalResult.ApprovedAndRemember
            ? FormatToolCalls(context.Requests)
            : null;

        // Use the first request's MessageId for correlation (requests in a batch typically share the same MessageId)
        var messageId = context.Requests.FirstOrDefault()?.MessageId;

        // Broadcast to all clients so other browsers can dismiss their approval modals and show tool calls
        await notifier.NotifyApprovalResolvedAsync(
                new ApprovalResolvedNotification(context.TopicId, approvalId, toolCalls, messageId))
            .SafeAwaitAsync(logger, "Failed to notify approval resolved for topic {TopicId}", context.TopicId);

        var success = context.TrySetResult(result);
        context.Dispose();
        return success;
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