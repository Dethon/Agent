using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Tools;

public record SetToolEvents(IReadOnlyList<ToolCallEvent> Events) : IAction;
public record AppendToolEvent(ToolCallEvent Event) : IAction;

public sealed class ToolsStore : Store<ToolsState>
{
    public ToolsStore() : base(new ToolsState()) { }

    public void SetEvents(IReadOnlyList<ToolCallEvent> events) =>
        Dispatch(new SetToolEvents(events), static (s, a) => s with { Events = a.Events });

    public void AppendEvent(ToolCallEvent evt) =>
        Dispatch(new AppendToolEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
