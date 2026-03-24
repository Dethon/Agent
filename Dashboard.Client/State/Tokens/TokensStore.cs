using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tokens;

public record SetTokenEvents(IReadOnlyList<TokenUsageEvent> Events) : IAction;
public record SetTokenBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetTokenGroupBy(TokenDimension GroupBy) : IAction;
public record SetTokenMetric(TokenMetric Metric) : IAction;
public record AppendTokenEvent(TokenUsageEvent Event) : IAction;
public record SetTokenDateRange(DateOnly From, DateOnly To) : IAction;

public sealed class TokensStore : Store<TokensState>
{
    public TokensStore() : base(new TokensState()) { }

    public void SetEvents(IReadOnlyList<TokenUsageEvent> events) =>
        Dispatch(new SetTokenEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetTokenBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(TokenDimension groupBy) =>
        Dispatch(new SetTokenGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(TokenMetric metric) =>
        Dispatch(new SetTokenMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendEvent(TokenUsageEvent evt) =>
        Dispatch(new AppendTokenEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetTokenDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
}
