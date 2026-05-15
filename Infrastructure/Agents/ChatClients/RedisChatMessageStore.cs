using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class RedisChatMessageStore(
    IThreadStateStore store,
    IMetricsPublisher? metricsPublisher = null,
    string? conversationId = null) : ChatHistoryProvider
{
    internal const string StateKey = "ChatHistoryProviderState";

    public static bool TryGetStateKey(AgentSession session, out string? stateKey)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.StateBag.TryGetValue<string>(StateKey, out var key) && !string.IsNullOrEmpty(key))
        {
            stateKey = key;
            return true;
        }
        stateKey = null;
        return false;
    }

    private readonly SemaphoreSlim _lock = new(1, 1);

    public override IReadOnlyList<string> StateKeys => [StateKey];

    private static string ResolveRedisKey(AgentSession session)
    {
        if (TryGetStateKey(session, out var key))
        {
            return key!;
        }

        var newKey = Guid.NewGuid().ToString();
        session.StateBag.SetValue(StateKey, newKey);
        return newKey;
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context.Session);
        var redisKey = ResolveRedisKey(context.Session);
        return await store.GetMessagesAsync(redisKey) ?? [];
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context.Session);
        var redisKey = ResolveRedisKey(context.Session);

        // ToChatResponse does not preserve AdditionalProperties on ChatMessage objects,
        // so response messages arrive without timestamps. Stamp them before persisting.
        var now = DateTimeOffset.UtcNow;
        foreach (var message in context.ResponseMessages?.Where(x => x.GetTimestamp() is null) ?? [])
        {
            message.SetTimestamp(now);
        }

        var newMessages = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .ToArray();

        // The lock serializes concurrent same-conversation turns through the one-time
        // legacy migration in AppendMessagesAsync and preserves per-turn message ordering.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await store.AppendMessagesAsync(redisKey, newMessages);
        }
        finally
        {
            _lock.Release();
        }
        sw.Stop();

        if (metricsPublisher is not null)
        {
            try
            {
                await metricsPublisher.PublishAsync(new LatencyEvent
                {
                    Stage = LatencyStage.HistoryStore,
                    DurationMs = sw.ElapsedMilliseconds,
                    ConversationId = conversationId
                }, cancellationToken);
            }
            catch
            {
                // Best-effort; persistence already succeeded.
            }
        }
    }
}