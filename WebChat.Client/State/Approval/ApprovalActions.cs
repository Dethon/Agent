using Domain.DTOs;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Approval;

public record ShowApproval(string TopicId, ToolApprovalRequestMessage Request) : IAction;

public record ApprovalResponding : IAction;

public record ApprovalResolved(string ApprovalId, string? ToolCalls) : IAction;

public record ClearApproval : IAction;

public record RespondToApproval(string ApprovalId, ToolApprovalResult Result) : IAction;