using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ScheduleAgentInfo(string Id, string Name, string? Description);