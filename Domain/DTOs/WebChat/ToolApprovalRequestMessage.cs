namespace Domain.DTOs.WebChat;

public record ToolApprovalRequestMessage(
    string ApprovalId,
    IReadOnlyList<ToolApprovalRequest> Requests);