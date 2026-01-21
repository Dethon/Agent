using Domain.DTOs;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Fixtures;

public sealed class FakeApprovalService : IApprovalService
{
    private readonly Dictionary<string, ToolApprovalRequestMessage> _pendingApprovals = new();
    private readonly List<(string ApprovalId, ToolApprovalResult Result)> _responses = new();

    public IReadOnlyList<(string ApprovalId, ToolApprovalResult Result)> Responses => _responses;

    public Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        _responses.Add((approvalId, result));
        return Task.FromResult(true);
    }

    public Task<ToolApprovalRequestMessage?> GetPendingApprovalForTopicAsync(string topicId)
    {
        return Task.FromResult(
            _pendingApprovals.TryGetValue(topicId, out var approval) ? approval : null);
    }

    public void SetPendingApproval(string topicId, ToolApprovalRequestMessage approval)
    {
        _pendingApprovals[topicId] = approval;
    }

    public void ClearPendingApproval(string topicId)
    {
        _pendingApprovals.Remove(topicId);
    }
}