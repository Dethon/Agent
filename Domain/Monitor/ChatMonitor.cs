using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IReadOnlyList<IChannelConnection> channels,
    IAgentFactory agentFactory,
    Func<IChannelConnection, string, IToolApprovalHandler> approvalHandlerFactory,
    ChatThreadResolver threadResolver,
    IMetricsPublisher metricsPublisher,
    IMemoryRecallHook? memoryRecallHook,
    ILogger<ChatMonitor> logger)
{
    public readonly record struct DeliveryTarget(IChannelConnection Channel, string ConversationId);

    public static async Task<IReadOnlyList<DeliveryTarget>> ResolveDeliveryTargetsAsync(
        ChannelMessage message,
        IChannelConnection originChannel,
        IReadOnlyList<IChannelConnection> channels,
        CancellationToken ct,
        ILogger? logger = null)
    {
        if (message.ReplyTo is not { Count: > 0 })
        {
            return [new DeliveryTarget(originChannel, message.ConversationId)];
        }

        var targets = new List<DeliveryTarget>();
        foreach (var target in message.ReplyTo)
        {
            var channel = channels.FirstOrDefault(c => c.ChannelId == target.ChannelId);
            if (channel is null)
            {
                continue;
            }

            var conversationId = target.ConversationId;
            if (conversationId is null)
            {
                try
                {
                    conversationId = await channel.CreateConversationAsync(
                        message.AgentId ?? "default", "Scheduled task", message.Sender, message.Content, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A target whose conversation can't be minted is skipped rather than
                    // aborting the whole fan-out (and the agent run that depends on it).
                    logger?.LogWarning(ex, "Failed to mint conversation on {ChannelId}; skipping target", target.ChannelId);
                    continue;
                }
            }

            if (conversationId is not null)
            {
                targets.Add(new DeliveryTarget(channel, conversationId));
            }
        }

        return targets;
    }

    public async Task Monitor(CancellationToken cancellationToken)
    {
        try
        {
            var merged = channels
                .Select(ch => ch.Messages.Select(m => (Channel: ch, Message: m)))
                .Merge(cancellationToken);

            var groups = merged
                .GroupByStreaming(
                    (x, _) => ValueTask.FromResult(new AgentKey(x.Message.ConversationId, x.Message.AgentId)),
                    cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            await foreach (var _ in groups)
            { }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "agent",
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            });
        }
    }

    private async IAsyncEnumerable<bool> ProcessChatThread(
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Resolve delivery targets BEFORE binding the approval handler and restoring
        // the thread. Two reasons:
        // 1) The persistence key for chat-history must match the first delivery
        //    target's conversation id — otherwise, when a target is minted (e.g. a
        //    schedule fire with a null ReplyTo conversationId), history persists
        //    under the synthetic group id while the receiving channel (WebChat)
        //    reads history keyed on the minted id and sees an empty conversation.
        // 2) The approval handler must route to the delivery target's channel, not
        //    the origin. Schedule/ServiceBus channels auto-approve silently, so
        //    binding to the origin would hide tool calls from the user in WebChat.
        // Resolved once per group; reused for every message.
        var first = await group.FirstAsync(ct);
        var targets = await ResolveDeliveryTargetsAsync(first.Message, first.Channel, channels, ct, logger);
        var (approvalChannel, approvalConversationId) = targets.Count > 0
            ? (targets[0].Channel, targets[0].ConversationId)
            : (first.Channel, first.Message.ConversationId);
        var approvalHandler = approvalHandlerFactory(approvalChannel, approvalConversationId);
        await using var agent = agentFactory.Create(agentKey, first.Message.Sender, first.Message.AgentId, approvalHandler);
        var context = threadResolver.Resolve(agentKey);

        var persistenceKey = targets.Count > 0
            ? new AgentKey(targets[0].ConversationId, first.Message.AgentId)
            : agentKey;
        var thread = await GetOrRestoreThread(agent, persistenceKey, ct);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // Start session warmup (MCP connections + tool discovery) without awaiting it
        // yet, so it overlaps with command parsing and memory recall. It is awaited
        // deterministically just before the first RunStreamingAsync below, so it never
        // outlives the agent and the order of operations is well-defined.
        var warmup = agent.WarmupSessionAsync(thread, linkedCt);

        var aiResponses = group.Prepend(first)
            .Select(async (x, _, _) =>
            {
                var command = ChatCommandParser.Parse(x.Message.Content);
                switch (command)
                {
                    case ChatCommand.Clear:
                        await threadResolver.ClearAsync(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate Update, AiResponse? Response)>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate Update, AiResponse? Response)>();
                    default:
                        var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
                        userMessage.SetSenderId(x.Message.Sender);
                        userMessage.SetLocation(x.Message.Location);
                        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
                        if (memoryRecallHook is not null)
                        {
                            await memoryRecallHook.EnrichAsync(userMessage, x.Message.Sender, x.Message.ConversationId, x.Message.AgentId, thread, linkedCt);
                        }

                        await warmup;
                        var stopwatch = Stopwatch.StartNew();
                        // ReSharper disable once AccessToDisposedClosure
                        return agent
                            .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
                            .WithErrorHandling(linkedCt)
                            .ToUpdateAiResponsePairs()
                            .Append((new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null))
                            .OnCompletion(
                                seed: false,
                                fold: (faulted, pair) => faulted || pair.Item1.Contents.OfType<ErrorContent>().Any(),
                                onCompletion: async (faulted, completionCt) =>
                                {
                                    var error = faulted ? "Agent run reported an error" : null;
                                    var evt = BuildScheduleEvent(x.Message, stopwatch.ElapsedMilliseconds, !faulted, error);
                                    if (evt is not null)
                                    {
                                        await metricsPublisher.PublishAsync(evt, completionCt);
                                    }
                                },
                                linkedCt);
                }
            })
            .Merge(linkedCt);

        await foreach (var (update, _) in aiResponses.WithCancellation(ct))
        {
            await DeliverUpdateAsync(update, targets, ct);
            yield return true;
        }
    }

    private async Task DeliverUpdateAsync(
        AgentResponseUpdate update, IReadOnlyList<DeliveryTarget> targets, CancellationToken ct)
    {
        foreach (var mapped in MapResponseUpdate(update))
        {
            foreach (var target in targets)
            {
                try
                {
                    await target.Channel.SendReplyAsync(
                        target.ConversationId, mapped.Content, mapped.ContentType, mapped.IsComplete, update.MessageId, ct);
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
                }
            }
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
    }

    public static ScheduleExecutionEvent? BuildScheduleEvent(
        ChannelMessage message, long durationMs, bool success, string? error)
    {
        if (message.Origin is not { Kind: MessageOriginKind.Schedule, ScheduleId: { } scheduleId })
        {
            return null;
        }

        return new ScheduleExecutionEvent
        {
            ScheduleId = scheduleId,
            AgentId = message.AgentId ?? "default",
            Prompt = message.Content,
            DurationMs = durationMs,
            Success = success,
            Error = error
        };
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

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}