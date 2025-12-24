using Domain.DTOs;
using Infrastructure.CliGui.Abstractions;
using Infrastructure.CliGui.Rendering;

namespace Tests.Unit.Infrastructure.Cli;

internal sealed class FakeTerminalAdapter : ITerminalAdapter
{
    private readonly List<ChatLine[]> _displayedMessages = [];

    public event Action<string>? InputReceived;
    public event Action? ShutdownRequested;

    public bool IsStarted { get; private set; }
    public bool IsStopped { get; private set; }
    public IReadOnlyList<ChatLine[]> DisplayedMessages => _displayedMessages;

    private static ToolApprovalResult NextApprovalResult => ToolApprovalResult.Approved;

    public void Start()
    {
        IsStarted = true;
    }

    public void Stop()
    {
        IsStopped = true;
    }

    public void DisplayMessage(ChatLine[] lines)
    {
        _displayedMessages.Add(lines);
    }

    public void ClearDisplay()
    {
        _displayedMessages.Clear();
    }

    public void ShowSystemMessage(string message)
    {
    }

    public void ShowToolResult(string toolName, IReadOnlyDictionary<string, object?> arguments,
        ToolApprovalResult resultType)
    {
    }

    public Task<ToolApprovalResult> ShowApprovalDialogAsync(string toolName, string details,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(NextApprovalResult);
    }

    public void SimulateInput(string input)
    {
        InputReceived?.Invoke(input);
    }

    public void SimulateShutdown()
    {
        ShutdownRequested?.Invoke();
    }

    public void Dispose() { }
}