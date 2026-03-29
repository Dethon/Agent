using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Metrics;

public record UpdateSummary(MetricsState Summary) : IAction;
public record IncrementFromTokenUsage(TokenUsageEvent Event) : IAction;
public record IncrementToolCall(bool IsError) : IAction;
public record IncrementMemoryRecall(int MemoryCount) : IAction;
public record IncrementMemoryExtraction(int StoredCount) : IAction;
public record IncrementMemoryDreaming(int MergedCount, int DecayedCount) : IAction;

public sealed class MetricsStore : Store<MetricsState>
{
    public MetricsStore() : base(new MetricsState()) { }

    public void UpdateSummary(MetricsState summary) =>
        Dispatch(new UpdateSummary(summary), static (_, a) => a.Summary);

    public void IncrementFromTokenUsage(TokenUsageEvent evt) =>
        Dispatch(new IncrementFromTokenUsage(evt), static (s, a) => s with
        {
            InputTokens = s.InputTokens + a.Event.InputTokens,
            OutputTokens = s.OutputTokens + a.Event.OutputTokens,
            Cost = s.Cost + a.Event.Cost,
        });

    public void IncrementToolCall(bool isError) =>
        Dispatch(new IncrementToolCall(isError), static (s, a) => s with
        {
            ToolCalls = s.ToolCalls + 1,
            ToolErrors = a.IsError ? s.ToolErrors + 1 : s.ToolErrors,
        });

    public void IncrementMemoryRecall(int memoryCount) =>
        Dispatch(new IncrementMemoryRecall(memoryCount), static (s, a) => s with
        {
            TotalRecalls = s.TotalRecalls + 1,
        });

    public void IncrementMemoryExtraction(int storedCount) =>
        Dispatch(new IncrementMemoryExtraction(storedCount), static (s, a) => s with
        {
            TotalExtractions = s.TotalExtractions + 1,
            MemoriesStored = s.MemoriesStored + a.StoredCount,
        });

    public void IncrementMemoryDreaming(int mergedCount, int decayedCount) =>
        Dispatch(new IncrementMemoryDreaming(mergedCount, decayedCount), static (s, a) => s with
        {
            TotalDreamings = s.TotalDreamings + 1,
            MemoriesMerged = s.MemoriesMerged + a.MergedCount,
            MemoriesDecayed = s.MemoriesDecayed + a.DecayedCount,
        });
}