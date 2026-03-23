using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Errors;

public record ErrorsState
{
    public IReadOnlyList<ErrorEvent> Events { get; init; } = [];
}
