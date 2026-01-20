using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Approval;

public sealed record ApprovalState
{
    public ToolApprovalRequestMessage? CurrentRequest { get; init; }
    public string? TopicId { get; init; }
    public bool IsResponding { get; init; }

    public static ApprovalState Initial => new();
}
