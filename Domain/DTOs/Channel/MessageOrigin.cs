using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record MessageOrigin(string Kind, string? ScheduleId);