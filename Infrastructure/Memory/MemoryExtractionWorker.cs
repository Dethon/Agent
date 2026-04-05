using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryExtractionOptions
{
    public double SimilarityThreshold { get; init; } = 0.85;
    public int MaxCandidatesPerMessage { get; init; } = 5;
    public int MaxRetries { get; init; } = 2;
    public int WindowMixedTurns { get; init; } = 6;
}

public class MemoryExtractionWorker(
    MemoryExtractionQueue queue,
    IMemoryExtractor extractor,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    IThreadStateStore threadStateStore,
    IMetricsPublisher metricsPublisher,
    IAgentDefinitionProvider agentDefinitionProvider,
    ILogger<MemoryExtractionWorker> logger,
    MemoryExtractionOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in queue.ReadAllAsync(ct))
            {
                await ProcessRequestAsync(request, ct);
            }

        }
        catch (OperationCanceledException) { }
    }

    public async Task ProcessRequestAsync(MemoryExtractionRequest request, CancellationToken ct)
    {
        if (request.AgentId is not null)
        {
            var agentDef = agentDefinitionProvider.GetById(request.AgentId);
            if (agentDef is not null && !agentDef.EnabledFeatures.Contains("memory", StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var sw = Stopwatch.StartNew();
        var storedCount = 0;
        var candidateCount = 0;

        try
        {
            var candidates = await ExtractWithRetryAsync(request, ct);
            candidateCount = candidates.Count;

            var storeResults = await Task.WhenAll(
                candidates.Take(options.MaxCandidatesPerMessage)
                    .Select(c => StoreIfNovelAsync(request.UserId, c, request.ConversationId, ct)));

            storedCount = storeResults.Count(stored => stored);

            sw.Stop();
            await metricsPublisher.PublishAsync(new MemoryExtractionEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                CandidateCount = candidateCount,
                StoredCount = storedCount,
                UserId = request.UserId,
                AgentId = request.AgentId is not null ? agentDefinitionProvider.GetById(request.AgentId)?.Name ?? request.AgentId : null,
                ConversationId = request.ConversationId
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory extraction failed for user {UserId}", request.UserId);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Extraction failed: {ex.Message}"
            }, ct);
        }
    }

    private async Task<IReadOnlyList<ExtractionCandidate>> ExtractWithRetryAsync(
        MemoryExtractionRequest request, CancellationToken ct)
    {
        var thread = await threadStateStore.GetMessagesAsync(request.ThreadStateKey);
        if (thread is null || request.AnchorIndex < 0 || request.AnchorIndex >= thread.Length)
        {
            if (!string.IsNullOrEmpty(request.FallbackContent))
            {
                return await ExtractWithFallbackAsync(request, ct);
            }

            logger.LogDebug(
                "Extraction dropped: thread missing or anchor out of range (user {UserId}, key {Key}, anchor {Anchor})",
                request.UserId, request.ThreadStateKey, request.AnchorIndex);
            return [];
        }

        var window = thread
            .Take(request.AnchorIndex + 1)
            .TakeLast(options.WindowMixedTurns)
            .ToList();

        if (window.Count == 0)
        {
            return [];
        }

        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return await extractor.ExtractAsync(window, request.UserId, ct);
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                logger.LogWarning(ex, "Extraction attempt {Attempt} failed for user {UserId}, retrying",
                    attempt + 1, request.UserId);
            }
        }
        return [];
    }

    private async Task<IReadOnlyList<ExtractionCandidate>> ExtractWithFallbackAsync(
        MemoryExtractionRequest request, CancellationToken ct)
    {
        logger.LogDebug(
            "Thread unavailable, falling back to direct message extraction (user {UserId}, key {Key})",
            request.UserId, request.ThreadStateKey);

        var window = new List<ChatMessage> { new(ChatRole.User, request.FallbackContent!) };
        return await extractor.ExtractAsync(window, request.UserId, ct);
    }

    private async Task<bool> StoreIfNovelAsync(
        string userId, ExtractionCandidate candidate, string? conversationId, CancellationToken ct)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(candidate.Content, ct);

        var similar = await store.SearchAsync(
            userId, queryEmbedding: embedding, categories: [candidate.Category], limit: 3, ct: ct);

        var bestMatch = similar.FirstOrDefault(s => s.Relevance > options.SimilarityThreshold);

        if (bestMatch is not null)
        {
            logger.LogDebug(
                "Skipping duplicate memory for user {UserId}: {Content} (similar to {ExistingId}, relevance {Relevance:F2})",
                userId, candidate.Content, bestMatch.Memory.Id, bestMatch.Relevance);
            return false;
        }

        var memory = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = candidate.Category,
            Content = candidate.Content,
            Context = candidate.Context,
            Importance = Math.Clamp(candidate.Importance, 0, 1),
            Confidence = Math.Clamp(candidate.Confidence, 0, 1),
            Embedding = embedding,
            Tags = candidate.Tags,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            Source = new MemorySource(conversationId, null)
        };

        await store.StoreAsync(memory, ct);
        return true;
    }
}
