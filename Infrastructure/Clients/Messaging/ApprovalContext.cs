using Domain.DTOs;

namespace Infrastructure.Clients.Messaging;

internal sealed class ApprovalContext : IDisposable
{
    private readonly TaskCompletionSource<ToolApprovalResult> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenRegistration _registration;

    public required string TopicId { get; init; }
    public required IReadOnlyList<ToolApprovalRequest> Requests { get; init; }

    public void Dispose()
    {
        _registration.Dispose();
    }

    public bool TrySetResult(ToolApprovalResult result)
    {
        return _tcs.TrySetResult(result);
    }

    public Task<ToolApprovalResult> WaitForApprovalAsync(CancellationToken cancellationToken)
    {
        _registration = cancellationToken.Register(() => _tcs.TrySetCanceled(cancellationToken));
        return _tcs.Task;
    }
}