using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryDreamingOptions
{
    public string CronSchedule { get; init; } = "0 3 * * *";
    public int DecayDays { get; init; } = 30;
    public double DecayFactor { get; init; } = 0.9;
    public double DecayFloor { get; init; } = 0.1;
    public MemoryCategory[] DecayExemptCategories { get; init; } = [MemoryCategory.Instruction];
    public int MaxRetries { get; init; } = 2;
    public int MaxMergePasses { get; init; } = 3;
}

public class MemoryDreamingService(
    IMemoryStore store,
    IMemoryConsolidator consolidator,
    IEmbeddingService embeddingService,
    IMetricsPublisher metricsPublisher,
    ICronValidator cronValidator,
    ILogger<MemoryDreamingService> logger,
    MemoryDreamingOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var next = cronValidator.GetNextOccurrence(options.CronSchedule, DateTime.UtcNow);
            if (next is null)
            {
                logger.LogWarning("Cron schedule '{Schedule}' returned no next occurrence, stopping", options.CronSchedule);
                return;
            }

            var delay = next.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            await RunDreamingAsync(ct);
        }
    }

    private async Task RunDreamingAsync(CancellationToken ct)
    {
        var userIds = await store.GetAllUserIdsAsync(ct);
        logger.LogInformation("Starting dreaming cycle for {UserCount} users", userIds.Count);

        var now = DateTimeOffset.UtcNow;
        foreach (var userId in userIds)
        {
            try
            {
                await RunDreamingForUserAsync(userId, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Dreaming failed for user {UserId}", userId);
            }
        }
    }

    public async Task RunDreamingForUserAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var activeMemories = await GetActiveMemoriesAsync(userId, ct);

        // Step 1: Merge — loop until consolidation stabilizes or the bound is hit
        var mergedCount = 0;
        for (var pass = 0; pass < options.MaxMergePasses; pass++)
        {
            var passMerges = await MergeAsync(userId, activeMemories, ct);
            if (passMerges == 0)
            {
                break;
            }

            mergedCount += passMerges;
            activeMemories = await GetActiveMemoriesAsync(userId, ct);
        }

        // Step 2: Decay
        var decayedCount = await DecayAsync(userId, activeMemories, now, ct);

        // Step 3: Reflect
        var profile = await consolidator.SynthesizeProfileAsync(userId, activeMemories, ct);
        await store.SaveProfileAsync(profile, ct);

        await metricsPublisher.PublishAsync(new MemoryDreamingEvent
        {
            MergedCount = mergedCount,
            DecayedCount = decayedCount,
            ProfileRegenerated = true,
            UserId = userId
        }, ct);

        logger.LogInformation(
            "Dreaming complete for {UserId}: {Merged} merged, {Decayed} decayed, profile regenerated",
            userId, mergedCount, decayedCount);
    }

    private async Task<int> MergeAsync(string userId, IReadOnlyList<MemoryEntry> activeMemories, CancellationToken ct)
    {
        var decisions = await consolidator.ConsolidateAsync(activeMemories, ct);
        var activeIds = activeMemories.Select(m => m.Id).ToHashSet();
        var mergedCount = 0;

        foreach (var decision in decisions)
        {
            var validSourceIds = decision.SourceIds.Where(activeIds.Contains).Distinct().ToList();

            switch (decision.Action)
            {
                case MergeAction.Merge when validSourceIds.Count >= 2:
                    await ApplyMergeAsync(userId, decision with { SourceIds = validSourceIds }, ct);
                    mergedCount++;
                    break;

                case MergeAction.SupersedeOlder when validSourceIds.Count >= 2:
                    await store.DeleteAsync(userId, validSourceIds[0], ct);
                    mergedCount++;
                    break;
            }
        }

        return mergedCount;
    }

    private async Task<IReadOnlyList<MemoryEntry>> GetActiveMemoriesAsync(string userId, CancellationToken ct)
    {
        return await store.GetByUserIdAsync(userId, ct);
    }

    private async Task ApplyMergeAsync(string userId, MergeDecision decision, CancellationToken ct)
    {
        var embedding = decision.MergedContent is not null
            ? await embeddingService.GenerateEmbeddingAsync(decision.MergedContent, ct)
            : null;

        var merged = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = decision.Category ?? MemoryCategory.Fact,
            Content = decision.MergedContent ?? string.Empty,
            Importance = decision.Importance ?? 0.5,
            Confidence = 0.9,
            Embedding = embedding,
            Tags = decision.Tags ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        var stored = await store.StoreAsync(merged, ct);

        foreach (var sourceId in decision.SourceIds)
        {
            await store.DeleteAsync(userId, sourceId, ct);
        }
    }

    private async Task<int> DecayAsync(
        string userId, IReadOnlyList<MemoryEntry> activeMemories, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-options.DecayDays);

        var eligible = activeMemories.Where(m =>
            m.LastAccessedAt < cutoff &&
            !options.DecayExemptCategories.Contains(m.Category) &&
            m.Importance * options.DecayFactor >= options.DecayFloor);

        var decayedCount = 0;
        foreach (var memory in eligible)
        {
            var newImportance = Math.Round(memory.Importance * options.DecayFactor, 2);
            await store.UpdateImportanceAsync(userId, memory.Id, newImportance, ct);
            decayedCount++;
        }

        return decayedCount;
    }
}
