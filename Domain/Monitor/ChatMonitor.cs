using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
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

    private sealed record TurnUpdate(
        AgentResponseUpdate Update, IReadOnlyList<DeliveryTarget> Targets, FirstReplyTracker? Tracker);

    private sealed record GroupAnchors(
        IReadOnlyList<DeliveryTarget> Targets, IToolApprovalHandler ApprovalHandler, AgentKey PersistenceKey);

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
        var first = await group.FirstAsync(ct);
        var anchors = await ResolveGroupAnchorsAsync(first, agentKey, ct);
        await using var agent = agentFactory.Create(agentKey, first.Message.Sender, first.Message.AgentId, anchors.ApprovalHandler);
        var context = threadResolver.Resolve(agentKey);
        var thread = await GetOrRestoreThread(agent, anchors.PersistenceKey, ct);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // Start session warmup (MCP connections + tool discovery) without awaiting it
        // yet, so it overlaps with command parsing and memory recall. It is awaited
        // deterministically just before the first RunStreamingAsync below, so it never
        // outlives the agent and the order of operations is well-defined.
        var warmup = agent.WarmupSessionAsync(thread, linkedCt);

        var aiResponses = group.Prepend(first)
            .Select(async (x, index, _) => await RunTurnAsync(x, index, agentKey, anchors.Targets, agent, thread, warmup, linkedCt))
            .Merge(linkedCt);

        await foreach (var turn in aiResponses.WithCancellation(ct))
        {
            var deliveredContent = await replyDispatcher.DeliverUpdateAsync(turn.Update, turn.Targets, ct);
            if (deliveredContent && turn.Tracker?.TryComplete() is { } firstReplyMs)
            {
                await PublishFirstReplyLatencyAsync(firstReplyMs, turn.Targets, agentKey, ct);
            }

            yield return true;
        }
    }

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
    // approval handler; per-message reply delivery is resolved separately in
    // ResolveTurnTargetsAsync.
    private async Task<GroupAnchors> ResolveGroupAnchorsAsync(
        (IChannelConnection Channel, ChannelMessage Message) first, AgentKey agentKey, CancellationToken ct)
    {
        var targets = await targetResolver.ResolveAsync(first.Message, first.Channel, ct);
        var (approvalChannel, approvalConversationId) = targets.Count > 0
            ? (targets[0].Channel, targets[0].ConversationId)
            : (first.Channel, first.Message.ConversationId);
        var approvalHandler = approvalHandlerFactory(approvalChannel, approvalConversationId);
        var persistenceKey = targets.Count > 0
            ? new AgentKey(targets[0].ConversationId, first.Message.AgentId)
            : agentKey;
        return new GroupAnchors(targets, approvalHandler, persistenceKey);
    }

    private async Task<IAsyncEnumerable<TurnUpdate>> RunTurnAsync(
        (IChannelConnection Channel, ChannelMessage Message) x,
        int index,
        AgentKey agentKey,
        IReadOnlyList<DeliveryTarget> groupTargets,
        DisposableAgent agent,
        AgentSession thread,
        Task warmup,
        CancellationToken ct)
    {
        switch (ChatCommandParser.Parse(x.Message.Content))
        {
            case ChatCommand.Clear:
                await threadResolver.ClearAsync(agentKey);
                return AsyncEnumerable.Empty<TurnUpdate>();
            case ChatCommand.Cancel:
                threadResolver.Cancel(agentKey);
                return AsyncEnumerable.Empty<TurnUpdate>();
        }

        // FirstReply times "message arrival → first delivered reply chunk":
        // started before target resolution, memory recall, session warmup, and
        // the turn-start announce for agent-initiated messages, so the
        // measurement includes every stage the user actually waits on.
        var tracker = new FirstReplyTracker();
        var targets = await ResolveTurnTargetsAsync(x, index, groupTargets, ct);
        // Agent-initiated turns (downloads, schedules) land in conversations
        // with no live stream on the receiving channel; announce the turn so
        // the channel can set one up before reply chunks arrive. Targets the
        // group-opening message minted were announced by their own
        // create_conversation; later messages reusing the group targets see
        // those conversations as pre-existing.
        if (x.Message.Origin is not null)
        {
            await targetResolver.AnnounceTurnStartAsync(targets, x.Message, skipMinted: index == 0, ct);
        }
        var userMessage = await BuildUserMessageAsync(x.Message, targets, thread, ct);

        await warmup;
        return StreamAgentTurn(agent, thread, userMessage, x.Message, targets, tracker, ct);
    }

    // Deliver each message's reply to the channel that actually sent it. The
    // group is keyed only by (ConversationId, AgentId), so a later message from
    // a different channel — e.g. the user typing in WebChat inside a
    // voice-started conversation — joins this same group. The group-level
    // targets cover the first/initiating message and any ReplyTo fan-out
    // (re-resolving the latter would re-mint conversations); a subsequent plain
    // interactive message is routed back to its own origin instead of the
    // opening channel.
    private async Task<IReadOnlyList<DeliveryTarget>> ResolveTurnTargetsAsync(
        (IChannelConnection Channel, ChannelMessage Message) x,
        int index,
        IReadOnlyList<DeliveryTarget> groupTargets,
        CancellationToken ct)
    {
        return index == 0 || x.Message.ReplyTo is { Count: > 0 }
            ? groupTargets
            : await targetResolver.ResolveAsync(x.Message, x.Channel, ct);
    }

    private async Task<ChatMessage> BuildUserMessageAsync(
        ChannelMessage message, IReadOnlyList<DeliveryTarget> targets, AgentSession thread, CancellationToken ct)
    {
        var userMessage = new ChatMessage(ChatRole.User, message.Content);
        userMessage.SetSenderId(message.Sender);
        userMessage.SetLocation(message.Location);
        userMessage.SetSatelliteId(message.SatelliteId);
        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
        userMessage.SetConversationContext(DeliveryTargetResolver.BuildConversationContext(message, targets));
        if (memoryRecallHook is not null)
        {
            await memoryRecallHook.EnrichAsync(userMessage, message.Sender, message.ConversationId, message.AgentId, thread, ct);
        }

        return userMessage;
    }

    private IAsyncEnumerable<TurnUpdate> StreamAgentTurn(
        DisposableAgent agent,
        AgentSession thread,
        ChatMessage userMessage,
        ChannelMessage message,
        IReadOnlyList<DeliveryTarget> targets,
        FirstReplyTracker tracker,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        // ReSharper disable once AccessToDisposedClosure
        return agent
            .RunStreamingAsync([userMessage], thread, cancellationToken: ct)
            .WithErrorHandling(ct)
            .ToUpdateAiResponsePairs()
            .Append((new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null))
            .OnCompletion(
                seed: false,
                fold: (faulted, pair) => faulted || pair.Item1.Contents.OfType<ErrorContent>().Any(),
                onCompletion: async (faulted, completionCt) =>
                {
                    var error = faulted ? "Agent run reported an error" : null;
                    var evt = ScheduleExecutionEvent.FromMessage(message, stopwatch.ElapsedMilliseconds, !faulted, error);
                    if (evt is not null)
                    {
                        await metricsPublisher.PublishAsync(evt, completionCt);
                    }
                },
                ct)
            .Select(pair => new TurnUpdate(pair.Item1, targets, tracker));
    }

    private async Task PublishFirstReplyLatencyAsync(
        long firstReplyMs, IReadOnlyList<DeliveryTarget> targets, AgentKey agentKey, CancellationToken ct)
    {
        await metricsPublisher.PublishAsync(new LatencyEvent
        {
            Stage = LatencyStage.FirstReply,
            DurationMs = firstReplyMs,
            // Attribute the event to where the reply actually landed (same idiom as
            // the persistence key in ResolveGroupAnchorsAsync): a scheduled fire
            // delivers to minted target conversations, not the scheduling channel's
            // own conversation id.
            ConversationId = targets.Count > 0 ? targets[0].ConversationId : agentKey.ConversationId
        }, ct);
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}