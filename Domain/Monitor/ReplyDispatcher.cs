using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ReplyDispatcher(IMetricsPublisher metricsPublisher, ILogger logger)
{
    public async Task<bool> DeliverUpdateAsync(
        AgentResponseUpdate update, IReadOnlyList<DeliveryTarget> targets, CancellationToken ct)
    {
        var deliveredContent = false;
        foreach (var mapped in MapResponseUpdate(update))
        {
            var results = await Task.WhenAll(targets.Select(target =>
                DeliverToTargetAsync(target, mapped, update.MessageId, ct)));
            deliveredContent |= mapped.ContentType != ReplyContentType.StreamComplete && results.Any(r => r);
        }

        foreach (var error in update.Contents.OfType<ErrorContent>())
        {
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "agent",
                ErrorType = error.ErrorCode ?? "Unknown",
                Message = error.Message
            }, ct);
        }

        return deliveredContent;
    }

    private async Task<bool> DeliverToTargetAsync(
        DeliveryTarget target,
        (string Content, ReplyContentType ContentType, bool IsComplete) mapped,
        string? messageId,
        CancellationToken ct)
    {
        try
        {
            await target.Channel.SendReplyAsync(
                target.ConversationId, mapped.Content, mapped.ContentType, mapped.IsComplete, messageId, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Isolate per-target delivery failures: one channel being down must not
            // abort delivery to the other targets or tear down the agent run (which
            // would also suppress its schedule-execution metric).
            logger.LogWarning(ex, "Failed to deliver reply to {ChannelId}; skipping target",
                target.Channel.ChannelId);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "agent",
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            }, ct);
            return false;
        }
    }

    private static IEnumerable<(string Content, ReplyContentType ContentType, bool IsComplete)> MapResponseUpdate(
        AgentResponseUpdate update)
    {
        foreach (var aiContent in update.Contents)
        {
            (string, ReplyContentType, bool)? mapped = aiContent switch
            {
                TextContent text when !string.IsNullOrEmpty(text.Text)
                    => (text.Text, ReplyContentType.Text, false),
                TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text)
                    => (reasoning.Text, ReplyContentType.Reasoning, false),
                // FunctionCallContent is intentionally skipped — tool calls are displayed
                // by the approval flow (request_approval tool with mode=request or mode=notify)
                ErrorContent error
                    => (error.Message, ReplyContentType.Error, false),
                StreamCompleteContent
                    => (string.Empty, ReplyContentType.StreamComplete, true),
                _ => null
            };

            if (mapped is { } value)
            {
                yield return value;
            }
        }
    }
}