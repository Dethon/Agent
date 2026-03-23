using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Errors;

public record ErrorsState
{
    public IReadOnlyList<ErrorEvent> Events { get; init; } = [];
    public ErrorDimension GroupBy { get; init; } = ErrorDimension.Service;
    public Dictionary<string, int> Breakdown { get; init; } = [];
}
