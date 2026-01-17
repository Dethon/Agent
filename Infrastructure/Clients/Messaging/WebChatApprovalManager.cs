using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatApprovalManager(
    WebChatStreamManager streamManager,
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
        var message = new ChatStreamMessage
        {
            ToolCalls = FormatToolCalls(requests)
        };

        await streamManager.WriteMessageAsync(topicId, message, cancellationToken);
    }

    public bool RespondToApproval(string approvalId, ToolApprovalResult result)
    {
        if (!_pendingApprovals.TryRemove(approvalId, out var context))
        {
            logger.LogWarning("RespondToApproval: approvalId {ApprovalId} not found or already processed", approvalId);
            return false;
        }

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