using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
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
    private readonly DeliveryTargetResolver targetResolver = new(channels, logger);
    private readonly ReplyDispatcher replyDispatcher = new(metricsPublisher, logger);

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
        // These first-message targets anchor the group-level persistence key and
        // approval handler; per-message reply delivery is resolved separately below.
        var first = await group.FirstAsync(ct);
        var targets = await targetResolver.ResolveAsync(first.Message, first.Channel, ct);
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
            .Select(async (x, index, _) =>
            {
                var command = ChatCommandParser.Parse(x.Message.Content);
                switch (command)
                {
                    case ChatCommand.Clear:
                        await threadResolver.ClearAsync(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate Update, IReadOnlyList<DeliveryTarget> Targets, FirstReplyTracker? Tracker)>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate Update, IReadOnlyList<DeliveryTarget> Targets, FirstReplyTracker? Tracker)>();
                    default:
                        // FirstReply times "message arrival → first delivered reply chunk":
                        // started before target resolution, memory recall, session warmup, and
                        // the turn-start announce for agent-initiated messages, so the
                        // measurement includes every stage the user actually waits on.
                        var tracker = new FirstReplyTracker();
                        // Deliver each message's reply to the channel that actually sent
                        // it. The group is keyed only by (ConversationId, AgentId), so a
                        // later message from a different channel — e.g. the user typing in
                        // WebChat inside a voice-started conversation — joins this same
                        // group. The group-level `targets` cover the first/initiating
                        // message and any ReplyTo fan-out (re-resolving the latter would
                        // re-mint conversations); a subsequent plain interactive message is
                        // routed back to its own origin instead of the opening channel.
                        var messageTargets = index == 0 || x.Message.ReplyTo is { Count: > 0 }
                            ? targets
                            : await targetResolver.ResolveAsync(x.Message, x.Channel, linkedCt);
                        // Agent-initiated turns (downloads, schedules) land in conversations
                        // with no live stream on the receiving channel; announce the turn so
                        // the channel can set one up before reply chunks arrive. Targets the
                        // group-opening message minted were announced by their own
                        // create_conversation; later messages reusing the group targets see
                        // those conversations as pre-existing.
                        if (x.Message.Origin is not null)
                        {
                            await targetResolver.AnnounceTurnStartAsync(messageTargets, x.Message, skipMinted: index == 0, linkedCt);
                        }
                        var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
                        userMessage.SetSenderId(x.Message.Sender);
                        userMessage.SetLocation(x.Message.Location);
                        userMessage.SetSatelliteId(x.Message.SatelliteId);
                        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
                        userMessage.SetConversationContext(DeliveryTargetResolver.BuildConversationContext(x.Message, messageTargets));
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
                                    var evt = ScheduleExecutionEvent.FromMessage(x.Message, stopwatch.ElapsedMilliseconds, !faulted, error);
                                    if (evt is not null)
                                    {
                                        await metricsPublisher.PublishAsync(evt, completionCt);
                                    }
                                },
                                linkedCt)
                            .Select(pair => (Update: pair.Item1, Targets: messageTargets, Tracker: (FirstReplyTracker?)tracker));
                }
            })
            .Merge(linkedCt);

        await foreach (var (update, replyTargets, tracker) in aiResponses.WithCancellation(ct))
        {
            var deliveredContent = await replyDispatcher.DeliverUpdateAsync(update, replyTargets, ct);
            if (deliveredContent && tracker?.TryComplete() is { } firstReplyMs)
            {
                await metricsPublisher.PublishAsync(new LatencyEvent
                {
                    Stage = LatencyStage.FirstReply,
                    DurationMs = firstReplyMs,
                    // Attribute the event to where the reply actually landed (same idiom as
                    // the persistenceKey above): a scheduled fire delivers to minted target
                    // conversations, not the scheduling channel's own conversation id.
                    ConversationId = replyTargets.Count > 0 ? replyTargets[0].ConversationId : agentKey.ConversationId
                }, ct);
            }

            yield return true;
        }
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}