using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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
    ChatThreadResolver threadResolver,
    IMetricsPublisher metricsPublisher,
    IMemoryRecallHook? memoryRecallHook,
    ILogger<ChatMonitor> logger)
{
    private readonly DeliveryTargetResolver _targetResolver = new(channels, logger);
    private readonly ReplyDispatcher _replyDispatcher = new(metricsPublisher, logger);

    private sealed record TurnUpdate(
        AgentResponseUpdate Update, IReadOnlyList<DeliveryTarget> Targets, FirstReplyTracker? Tracker);

    private sealed record GroupAnchors(
        IReadOnlyList<DeliveryTarget> Targets, IChannelConnection ApprovalChannel, AgentKey DeliveryKey);

    // Everything a turn needs that is fixed for the whole conversation group.
    private sealed record TurnScope(
        AgentKey AgentKey,
        AgentKey DeliveryKey,
        IReadOnlyList<DeliveryTarget> Targets,
        DisposableAgent Agent,
        AgentSession Thread,
        Task Warmup);

    private sealed record PendingTurn(
        (IChannelConnection Channel, ChannelMessage Message) Source, int Index);

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
        await using var agent = agentFactory.Create(
            anchors.DeliveryKey, first.Message.Sender, first.Message.AgentId, anchors.ApprovalChannel);
        var context = threadResolver.Resolve(agentKey);
        var thread = await GetOrRestoreThread(agent, anchors.DeliveryKey, ct);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // Start session warmup (MCP connections + tool discovery) without awaiting it
        // yet, so it overlaps with command parsing and memory recall. It is awaited
        // deterministically just before the first RunStreamingAsync below, so it never
        // outlives the agent and the order of operations is well-defined.
        var warmup = agent.WarmupSessionAsync(thread, linkedCt);

        var scope = new TurnScope(agentKey, anchors.DeliveryKey, anchors.Targets, agent, thread, warmup);
        var aiResponses = RunTurnsSequentiallyAsync(group.Prepend(first), scope, linkedCt);

        await foreach (var turn in aiResponses.WithCancellation(ct))
        {
            var deliveredContent = await _replyDispatcher.DeliverUpdateAsync(turn.Update, turn.Targets, ct);
            if (deliveredContent && turn.Tracker?.TryComplete() is { } firstReplyMs)
            {
                await PublishFirstReplyLatencyAsync(firstReplyMs, anchors.DeliveryKey.ConversationId, ct);
            }

            yield return true;
        }
    }

    // One turn at a time within a conversation. Three pieces of state shared across a
    // conversation's turns depend on this and are not defended anywhere else:
    // ToolApprovalChatClient's dynamically-approved tool set (an unsynchronised HashSet
    // mutated mid-turn), and OpenRouterChatClient's reasoning queue and cost/cached-token
    // queues (drained per update and per response, so two interleaved streams on one client
    // cross-attribute each other's values). Reintroducing concurrency here re-breaks all
    // three. Different conversations and the fan-out across delivery targets stay concurrent.
    //
    // Commands do NOT queue: /cancel is how the stop button reaches the monitor, so it has to
    // reach threadResolver while the turn it stops is still running. Reading messages in a
    // separate loop keeps commands immediate and turns sequential.
    private async IAsyncEnumerable<TurnUpdate> RunTurnsSequentiallyAsync(
        IAsyncEnumerable<(IChannelConnection Channel, ChannelMessage Message)> messages,
        TurnScope scope,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pending = Channel.CreateUnbounded<PendingTurn>();
        var reader = DispatchCommandsAndQueueTurnsAsync(messages, scope.AgentKey, pending.Writer, ct);

        try
        {
            await foreach (var (x, index) in pending.Reader.ReadAllAsync(ct).IgnoreCancellation(ct))
            {
                var turn = await RunTurnAsync(x, index, scope, ct);
                await foreach (var update in turn.IgnoreCancellation(ct))
                {
                    yield return update;
                }
            }
        }
        finally
        {
            await reader;
        }
    }

    // The index counts every message in the group, commands included, because
    // ResolveTurnTargetsAsync reads index 0 as "the message the group anchors were resolved
    // from".
    private async Task DispatchCommandsAndQueueTurnsAsync(
        IAsyncEnumerable<(IChannelConnection Channel, ChannelMessage Message)> messages,
        AgentKey agentKey,
        ChannelWriter<PendingTurn> writer,
        CancellationToken ct)
    {
        var index = 0;
        try
        {
            await foreach (var x in messages.IgnoreCancellation(ct))
            {
                switch (ChatCommandParser.Parse(x.Message.Content))
                {
                    case ChatCommand.Clear:
                        await threadResolver.ClearAsync(agentKey);
                        break;
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        break;
                    default:
                        await writer.WriteAsync(new PendingTurn(x, index), ct);
                        break;
                }

                index++;
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    // Resolve delivery targets BEFORE anything downstream, because the turn's whole
    // identity comes out of them. The delivery identity is the first delivery target's
    // conversation id, or the message's own when nothing resolved, and it is the id
    // everything the turn produces is filed under: the agent is built from it, chat
    // history restores under it, approvals route to it, and every event it publishes is
    // stamped with it. The rule is "name the conversation the reply actually landed in".
    // A schedule fire delivers into a minted WebChat conversation, so filing any of that
    // under the synthetic scheduling id would name a conversation nobody can open — and
    // for chat history it is worse than a label: WebChat reads history keyed on the
    // minted id and would see an empty conversation.
    //
    // The approval channel follows the same anchor, not the origin. Schedule/ServiceBus
    // channels auto-approve silently, so binding approvals to the origin would hide tool
    // calls from the user in WebChat.
    //
    // These first-message targets anchor the group; per-message reply delivery is
    // resolved separately in ResolveTurnTargetsAsync.
    private async Task<GroupAnchors> ResolveGroupAnchorsAsync(
        (IChannelConnection Channel, ChannelMessage Message) first, AgentKey agentKey, CancellationToken ct)
    {
        var targets = await _targetResolver.ResolveAsync(first.Message, first.Channel, ct);
        var (deliveryChannel, deliveryKey) = targets.Count > 0
            ? (targets[0].Channel, new AgentKey(targets[0].ConversationId, first.Message.AgentId))
            : (first.Channel, agentKey);
        return new GroupAnchors(targets, deliveryChannel, deliveryKey);
    }

    private async Task<IAsyncEnumerable<TurnUpdate>> RunTurnAsync(
        (IChannelConnection Channel, ChannelMessage Message) x,
        int index,
        TurnScope scope,
        CancellationToken ct)
    {
        // FirstReply times "message arrival → first delivered reply chunk":
        // started before target resolution, memory recall, session warmup, and
        // the turn-start announce for agent-initiated messages, so the
        // measurement includes every stage the user actually waits on.
        var tracker = new FirstReplyTracker();
        var targets = await ResolveTurnTargetsAsync(x, index, scope.Targets, ct);
        // Agent-initiated turns (downloads, schedules) land in conversations
        // with no live stream on the receiving channel; announce the turn so
        // the channel can set one up before reply chunks arrive. Targets the
        // group-opening message minted were announced by their own
        // create_conversation; later messages reusing the group targets see
        // those conversations as pre-existing.
        if (x.Message.Origin is not null)
        {
            await _targetResolver.AnnounceTurnStartAsync(targets, x.Message, skipMinted: index == 0, ct);
        }
        var userMessage = await BuildUserMessageAsync(x.Message, targets, scope, ct);

        await scope.Warmup;
        return StreamAgentTurn(scope.Agent, scope.Thread, userMessage, x.Message, targets, tracker, ct);
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
            : await _targetResolver.ResolveAsync(x.Message, x.Channel, ct);
    }

    private async Task<ChatMessage> BuildUserMessageAsync(
        ChannelMessage message, IReadOnlyList<DeliveryTarget> targets, TurnScope scope, CancellationToken ct)
    {
        var userMessage = new ChatMessage(ChatRole.User, message.Content);
        userMessage.SetSenderId(message.Sender);
        userMessage.SetLocation(message.Location);
        userMessage.SetSatelliteId(message.SatelliteId);
        userMessage.SetDismissedAlert(message.DismissedAlert);
        userMessage.SetConfigPatch(message.ConfigPatch);
        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
        userMessage.SetConversationContext(DeliveryTargetResolver.BuildConversationContext(message, targets));
        if (memoryRecallHook is not null)
        {
            // The delivery identity again, not the message's own: recall stamps durable
            // provenance on any memory extracted from this turn, so the source it names
            // has to be a conversation that can still be opened.
            await memoryRecallHook.EnrichAsync(
                userMessage, message.Sender, scope.DeliveryKey.ConversationId, message.AgentId, scope.Thread, ct);
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
        long firstReplyMs, string deliveryConversationId, CancellationToken ct)
    {
        await metricsPublisher.PublishAsync(new LatencyEvent
        {
            Stage = LatencyStage.FirstReply,
            DurationMs = firstReplyMs,
            ConversationId = deliveryConversationId
        }, ct);
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}