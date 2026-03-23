using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Tokens;

public record SetTokenEvents(IReadOnlyList<TokenUsageEvent> Events) : IAction;
public record SetTokenBreakdowns(Dictionary<string, long> ByUser, Dictionary<string, long> ByModel) : IAction;
public record AppendTokenEvent(TokenUsageEvent Event) : IAction;

public sealed class TokensStore : Store<TokensState>
{
    public TokensStore() : base(new TokensState()) { }

    public void SetEvents(IReadOnlyList<TokenUsageEvent> events) =>
        Dispatch(new SetTokenEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdowns(Dictionary<string, long> byUser, Dictionary<string, long> byModel) =>
        Dispatch(new SetTokenBreakdowns(byUser, byModel), static (s, a) => s with
        {
            ByUser = a.ByUser,
            ByModel = a.ByModel,
        });

    public void AppendEvent(TokenUsageEvent evt) =>
        Dispatch(new AppendTokenEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
