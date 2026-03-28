using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryRecallOptions
{
    public int DefaultLimit { get; init; } = 10;
    public bool IncludePersonalityProfile { get; init; } = true;
}

public class MemoryRecallHook(
    IMemoryStore store,
    IEmbeddingService embeddingService,
    MemoryExtractionQueue extractionQueue,
    IMetricsPublisher metricsPublisher,
    ILogger<MemoryRecallHook> logger,
    MemoryRecallOptions options) : IMemoryRecallHook
{
    public async Task EnrichAsync(ChatMessage message, string userId, string? conversationId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var messageText = message.Text;
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            var embedding = await embeddingService.GenerateEmbeddingAsync(messageText, ct);

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

            // Update access timestamps fire-and-forget
            _ = Task.WhenAll(memories.Select(m => store.UpdateAccessAsync(userId, m.Memory.Id, ct)));

            // Enqueue extraction request (non-blocking)
            await extractionQueue.EnqueueAsync(
                new MemoryExtractionRequest(userId, messageText, conversationId), ct);

            sw.Stop();
            await metricsPublisher.PublishAsync(new MemoryRecallEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                MemoryCount = memories.Count,
                UserId = userId
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
}
