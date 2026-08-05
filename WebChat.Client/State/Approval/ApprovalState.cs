using System.Collections.Immutable;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Approval;

public sealed record PendingApproval(string TopicId, ToolApprovalRequestMessage Request);

public sealed record ApprovalState
{
    // Every request still waiting for an answer, oldest first. A single slot let a second
    // conversation's request hide the first one with no way to bring it back.
    public ImmutableList<PendingApproval> Pending { get; init; } = [];

    // The prompt on screen is the oldest one still waiting; answering it surfaces the next.
    public ToolApprovalRequestMessage? CurrentRequest => Pending.FirstOrDefault()?.Request;

    public string? TopicId => Pending.FirstOrDefault()?.TopicId;

    public static ApprovalState Initial => new();
}