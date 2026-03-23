using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Schedules;

public record SetScheduleEvents(IReadOnlyList<ScheduleExecutionEvent> Events) : IAction;
public record AppendScheduleEvent(ScheduleExecutionEvent Event) : IAction;

public sealed class SchedulesStore : Store<SchedulesState>
{
    public SchedulesStore() : base(new SchedulesState()) { }

    public void SetEvents(IReadOnlyList<ScheduleExecutionEvent> events) =>
        Dispatch(new SetScheduleEvents(events), static (s, a) => s with { Events = a.Events });

    public void AppendEvent(ScheduleExecutionEvent evt) =>
        Dispatch(new AppendScheduleEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
