using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Metrics;

public record UpdateSummary(MetricsState Summary) : IAction;
public record IncrementFromTokenUsage(TokenUsageEvent Event) : IAction;
public record IncrementToolCall(bool IsError) : IAction;

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
}
