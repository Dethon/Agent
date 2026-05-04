using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record RequestApprovalParams
{
    public required string ConversationId { get; init; }
    public required ApprovalMode Mode { get; init; }
    public required string Requests { get; init; }
}