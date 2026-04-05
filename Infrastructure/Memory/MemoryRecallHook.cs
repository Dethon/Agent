using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Memory;
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
    public async Task EnrichAsync(
        ChatMessage message,
        string userId,
        string? conversationId,
        string? agentId,
        AgentSession thread,
        CancellationToken ct)
    {
        if (agentId is not null)
        {
            var agentDef = agentDefinitionProvider.GetById(agentId);
            if (agentDef is not null && !agentDef.EnabledFeatures.Contains("memory", StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var messageText = message.Text;
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            var (persisted, stateKey) = await TryFetchThreadAsync(thread);
            var anchorIndex = persisted?.Length ?? 0;

            var embeddingInput = BuildRecallWindowText(messageText, persisted, options.WindowUserTurns);

            var embedding = await embeddingService.GenerateEmbeddingAsync(embeddingInput, ct);

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
                    await metricsPublisher.PublishAsync(new ErrorEvent
                    {
                        Service = "memory",
                        ErrorType = ex.GetType().Name,
                        Message = $"Access timestamp update failed: {ex.Message}"
                    });
                }
            });

            if (stateKey is not null)
            {
                await extractionQueue.EnqueueAsync(
                    new MemoryExtractionRequest(userId, stateKey, anchorIndex, conversationId, agentId)
                    {
                        FallbackContent = messageText
                    }, ct);
            }

            sw.Stop();
            await metricsPublisher.PublishAsync(new MemoryRecallEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                MemoryCount = memories.Count,
                UserId = userId,
                ConversationId = conversationId,
                AgentId = agentId is not null ? agentDefinitionProvider.GetById(agentId)?.Name ?? agentId : null
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory recall failed for user {UserId}", userId);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Recall failed: {ex.Message}"
            }, ct);
        }
    }

    private async Task<(ChatMessage[]? Messages, string? StateKey)> TryFetchThreadAsync(AgentSession thread)
    {
        if (!RedisChatMessageStore.TryGetStateKey(thread, out var stateKey) || stateKey is null)
        {
            return (null, null);
        }

        try
        {
            var messages = await threadStateStore.GetMessagesAsync(stateKey);
            return (messages, stateKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch thread history for recall window (key {Key})", stateKey);
            return (null, null);
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
