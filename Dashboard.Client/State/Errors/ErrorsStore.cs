using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Errors;

public record SetErrorEvents(IReadOnlyList<ErrorEvent> Events) : IAction;
public record AppendErrorEvent(ErrorEvent Event) : IAction;

public sealed class ErrorsStore : Store<ErrorsState>
{
    public ErrorsStore() : base(new ErrorsState()) { }

    public void SetEvents(IReadOnlyList<ErrorEvent> events) =>
        Dispatch(new SetErrorEvents(events), static (s, a) => s with { Events = a.Events });

    public void AppendEvent(ErrorEvent evt) =>
        Dispatch(new AppendErrorEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
