using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients.Cli;

public sealed class CliToolApprovalHandler : IToolApprovalHandler
{
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, ApprovalContext> _pendingApprovals = new();

    private string? _currentApprovalId;

    public CliToolApprovalHandler(ITerminalAdapter terminalAdapter, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(terminalAdapter);
        _terminalAdapter = terminalAdapter;
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<bool> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var approvalId = Guid.NewGuid().ToString("N")[..8];
        var context = new ApprovalContext();
        _pendingApprovals[approvalId] = context;
        _currentApprovalId = approvalId;

        try
        {
            var message = FormatApprovalMessage(requests);
            DisplayApprovalRequest(message);

            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                return await context.WaitForApprovalAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _terminalAdapter.ShowSystemMessage("⏱️ Tool approval timed out. Execution rejected.");
                return false;
            }
        }
        finally
        {
            _currentApprovalId = null;
            _pendingApprovals.TryRemove(approvalId, out _);
        }
    }

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var message = FormatAutoApprovedMessage(requests);
        DisplayAutoApproved(message);
        return Task.CompletedTask;
    }

    public bool TryHandleApprovalInput(string input)
    {
        if (_currentApprovalId is null)
        {
            return false;
        }

        if (!_pendingApprovals.TryGetValue(_currentApprovalId, out var context))
        {
            return false;
        }

        var normalizedInput = input.Trim().ToLowerInvariant();

        switch (normalizedInput)
        {
            case "y":
            case "yes":
            case "approve":
                context.SetResult(true);
                _terminalAdapter.ShowSystemMessage("✅ Tool approved");
                return true;

            case "n":
            case "no":
            case "reject":
                context.SetResult(false);
                _terminalAdapter.ShowSystemMessage("❌ Tool rejected");
                return true;

            default:
                return false;
        }
    }

    public bool HasPendingApproval => _currentApprovalId is not null;

    private void DisplayApprovalRequest(string message)
    {
        var chatMessage = new ChatMessage(
            "[Approval]",
            message,
            false,
            false,
            true,
            DateTime.Now);
        var lines = ChatMessageFormatter.FormatMessage(chatMessage).ToArray();
        _terminalAdapter.DisplayMessage(lines);
    }

    private void DisplayAutoApproved(string message)
    {
        var chatMessage = new ChatMessage(
            "[Auto-Approved]",
            message,
            false,
            true,
            false,
            DateTime.Now);
        var lines = ChatMessageFormatter.FormatMessage(chatMessage).ToArray();
        _terminalAdapter.DisplayMessage(lines);
    }

    private static string FormatApprovalMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🔧 Tool Approval Required");
        sb.AppendLine();

        foreach (var request in requests)
        {
            AppendToolDetails(sb, request);
        }

        sb.AppendLine("Type 'y' to approve or 'n' to reject:");

        return sb.ToString().TrimEnd();
    }

    private static string FormatAutoApprovedMessage(IReadOnlyList<ToolApprovalRequest> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("✅ Tool Auto-Approved");
        sb.AppendLine();

        foreach (var request in requests)
        {
            AppendToolDetails(sb, request);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendToolDetails(StringBuilder sb, ToolApprovalRequest request)
    {
        sb.AppendLine($"Tool: {request.ToolName}");

        if (request.Arguments.Count > 0)
        {
            sb.AppendLine("Arguments:");
            var json = JsonSerializer.Serialize(request.Arguments, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            sb.AppendLine(json);
        }

        sb.AppendLine();
    }

    private sealed class ApprovalContext
    {
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetResult(bool approved)
        {
            _tcs.TrySetResult(approved);
        }

        public Task<bool> WaitForApprovalAsync(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _tcs.TrySetCanceled(cancellationToken));
            return _tcs.Task;
        }
    }
}

public sealed class CliToolApprovalHandlerFactory(CliToolApprovalHandler handler) : IToolApprovalHandlerFactory
{
    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        return handler;
    }
}