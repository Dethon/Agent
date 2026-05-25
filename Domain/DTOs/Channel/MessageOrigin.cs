using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record MessageOrigin(MessageOriginKind Kind, string? ScheduleId);