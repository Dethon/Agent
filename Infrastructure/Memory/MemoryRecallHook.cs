using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Memory;
using Domain.Metrics;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryRecallOptions
{
    public int DefaultLimit { get; init; } = 10;
    public bool IncludePersonalityProfile { get; init; } = true;
    public int WindowUserTurns { get; init; } = 3;
    public int RecallTailMessages { get; init; } = 200;
}

public class MemoryRecallHook(
    IMemoryStore store,
    IEmbeddingService embeddingService,
    IThreadStateStore threadStateStore,
    MemoryExtractionQueue extractionQueue,
    IMetricsPublisher metricsPublisher,
    IAgentDefinitionProvider agentDefinitionProvider,
    ILogger<MemoryRecallHook> logger,
    MemoryRecallOptions options) : IMemoryRecallHook
{
    public const string EmbeddingErrorService = "memory-embedding";

    public async Task EnrichAsync(
        ChatMessage message,
        string userId,
        string? conversationId,
        string? agentId,
        AgentSession thread,
        CancellationToken ct)
    {
        if (!agentDefinitionProvider.HasFeatureEnabled(agentId, "memory"))
        {
            return;
        }

        try
        {
            var messageText = message.Text;
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            // Opened here rather than where the stopwatch used to start, which was above the guard:
            // the scope publishes on disposal, so opening it earlier would report a recall latency
            // for a recall that never happened. The guard is a string emptiness check, so nothing
            // measurable moves.
            var agentName = agentId is not null
                ? agentDefinitionProvider.GetById(agentId)?.Name ?? agentId
                : null;
            using var latency = metricsPublisher.MeasureLatency(
                LatencyStage.MemoryRecall, conversationId, agentName);

            // Read before the agent is handed this turn, which is what the anchor's factory
            // says it needs and what the chat monitor test pins.
            var (persisted, persistedCount, stateKey) = await TryFetchThreadAsync(thread);
            var anchor = MemoryAnchor.TakenBeforeCurrentTurnIsPersisted(persistedCount);

            var embeddingInput = BuildRecallWindowText(messageText, persisted, options.WindowUserTurns);

            // Timed on its own so an operator can say how much of a recall was the embedding
            // round trip and how much was everything else, rather than inferring it from the
            // difference between the stage and what storage is known to cost. The scope
            // publishes on disposal, so a failed call is measured too.
            float[] embedding;
            using (metricsPublisher.MeasureLatency(LatencyStage.MemoryEmbedding, conversationId, agentName))
            {
                embedding = await EmbedAsync(embeddingInput, userId, ct);
            }

            var memoriesTask = store.SearchAsync(userId, queryEmbedding: embedding, limit: options.DefaultLimit, ct: ct);
            var profileTask = options.IncludePersonalityProfile
                ? store.GetProfileAsync(userId, ct)
                : Task.FromResult<PersonalityProfile?>(null);

            await Task.WhenAll(memoriesTask, profileTask);

            var memories = await memoriesTask;
            var profile = await profileTask;

            if (memories.Count > 0 || profile is not null)
            {
                message.SetMemoryContext(new MemoryContext(memories, profile));
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(memories.Select(m => store.UpdateAccessAsync(userId, m.Memory.Id, CancellationToken.None)));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to update access timestamps for user {UserId}", userId);
                    metricsPublisher.Publish(new ErrorEvent
                    {
                        Service = "memory",
                        ErrorType = ex.GetType().Name,
                        Message = $"Access timestamp update failed: {ex.Message}"
                    });
                }
            });

            await extractionQueue.EnqueueAsync(
                new MemoryExtractionRequest(userId, stateKey, anchor, conversationId, agentId)
                {
                    FallbackContent = messageText
                }, ct);

            // Same duration as the latency event the scope publishes, taken off the scope rather
            // than from a second stopwatch.
            metricsPublisher.Publish(new MemoryRecallEvent
            {
                DurationMs = latency.ElapsedMilliseconds,
                MemoryCount = memories.Count,
                UserId = userId,
                ConversationId = conversationId,
                AgentId = agentName
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory recall failed for user {UserId}", userId);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Recall failed: {ex.Message}"
            });
        }
    }

    private async Task<float[]> EmbedAsync(string input, string userId, CancellationToken ct)
    {
        try
        {
            return await embeddingService.GenerateEmbeddingAsync(input, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // There is deliberately no fallback to a hosted provider. Hosted vectors are
            // 1536 wide against a 1024-wide index, so they would be invalid rather than
            // merely slower, and every search against them would error. A failure here
            // degrades to a turn with no recall block, which is what already happened when
            // the hosted provider failed. See
            // docs/adr/0019-recall-embeds-locally-with-no-cross-provider-fallback.md.
            //
            // Published under its own service name so an embedding outage is distinguishable
            // from an ordinary recall that simply found nothing.
            logger.LogWarning(ex, "Embedding the recall window failed for user {UserId}", userId);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = EmbeddingErrorService,
                ErrorType = ex.GetType().Name,
                Message = $"Embedding failed: {ex.Message}"
            });
            throw;
        }
    }

    private async Task<(ChatMessage[]? Messages, long Count, string? StateKey)> TryFetchThreadAsync(AgentSession thread)
    {
        if (!RedisChatMessageStore.TryGetStateKey(thread, out var stateKey) || stateKey is null)
        {
            return (null, 0, null);
        }

        try
        {
            var countTask = threadStateStore.GetMessageCountAsync(stateKey);
            var tailTask = threadStateStore.GetTailMessagesAsync(stateKey, options.RecallTailMessages);
            await Task.WhenAll(countTask, tailTask);
            return (await tailTask, await countTask, stateKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch thread history for recall window (key {Key})", stateKey);
            return (null, 0, stateKey);
        }
    }

    private static string BuildRecallWindowText(string currentText, ChatMessage[]? persisted, int windowUserTurns)
    {
        if (persisted is null || persisted.Length == 0 || windowUserTurns <= 1)
        {
            return currentText;
        }

        var lines = persisted
            .Where(m => m.Role == ChatRole.User)
            .TakeLast(windowUserTurns - 1)
            .Select(m => m.Text)
            .Append(currentText);

        return string.Join("\n", lines);
    }
}