using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Schedules;

public record SchedulesState
{
    public IReadOnlyList<ScheduleExecutionEvent> Events { get; init; } = [];
    public ScheduleDimension GroupBy { get; init; } = ScheduleDimension.Schedule;
    public Dictionary<string, int> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}