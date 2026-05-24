using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record RequestApprovalParams
{
    public required string ConversationId { get; init; }
    public required ApprovalMode Mode { get; init; }
    public required IReadOnlyList<ToolApprovalRequest> Requests { get; init; }
}