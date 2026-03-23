using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Schedules;

public record SchedulesState
{
    public IReadOnlyList<ScheduleExecutionEvent> Events { get; init; } = [];
}
