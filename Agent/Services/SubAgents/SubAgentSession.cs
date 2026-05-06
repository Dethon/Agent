using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Microsoft.Extensions.AI;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSession : IAsyncDisposable
{
    public string Handle { get; }
    public SubAgentDefinition Profile { get; }
    public bool Silent { get; }
    public string ReplyToConversationId { get; }
    public IChannelConnection? ReplyChannel { get; }
    public DateTimeOffset StartedAt { get; }

    private readonly Func<DisposableAgent> _agentFactory;
    private readonly string _prompt;
    private readonly List<SubAgentTurnSnapshot> _turns = [];
    private readonly object _turnsLock = new();
    private readonly CancellationTokenSource _cts = new();

    // -1 = Running; else cast to SubAgentStatus
    private int _terminalState = -1;

    // -1 = not set; else cast to SubAgentCancelSource
    private int _cancelledBySource = -1;

    private SubAgentSessionError? _error;
    private string? _finalResult;
    private DisposableAgent? _agent;

    public SubAgentSession(
        string handle,
        SubAgentDefinition profile,
        string prompt,
        bool silent,
        Func<DisposableAgent> agentFactory,
        string replyToConversationId,
        IChannelConnection? replyChannel = null,
        DateTimeOffset? now = null)
    {
        Handle = handle;
        Profile = profile;
        Silent = silent;
        _prompt = prompt;
        _agentFactory = agentFactory;
        ReplyToConversationId = replyToConversationId;
        ReplyChannel = replyChannel;
        StartedAt = now ?? DateTimeOffset.UtcNow;
    }

    public bool IsTerminal => Volatile.Read(ref _terminalState) >= 0;

    public SubAgentCancelSource? CancelledBy
    {
        get
        {
            var v = Volatile.Read(ref _cancelledBySource);
            return v < 0 ? null : (SubAgentCancelSource)v;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _agent = _agentFactory();
        var maxMs = Math.Max(1, Profile.MaxExecutionSeconds) * 1000;
        _cts.CancelAfter(maxMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);

        try
        {
            var userMsg = new ChatMessage(ChatRole.User, _prompt);
            var stream = _agent.RunStreamingAsync([userMsg], cancellationToken: linked.Token);
            var recorder = new SnapshotRecorder();

            await foreach (var update in stream.WithCancellation(linked.Token))
            {
                if (recorder.OnUpdate(update) is { } completedTurn)
                {
                    lock (_turnsLock) _turns.Add(completedTurn);
                }
            }

            var lastTurn = recorder.Flush();
            if (lastTurn is not null)
            {
                lock (_turnsLock) _turns.Add(lastTurn);
            }

            Volatile.Write(ref _finalResult, recorder.FinalAssistantText);
            TrySetTerminal(SubAgentStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            var src = Volatile.Read(ref _cancelledBySource);
            if (src < 0)
            {
                // No caller set a source — timed out via _cts
                Interlocked.CompareExchange(ref _cancelledBySource, (int)SubAgentCancelSource.System, -1);
                Volatile.Write(ref _error, new SubAgentSessionError("Timeout",
                    $"Subagent '{Profile.Id}' exceeded {Profile.MaxExecutionSeconds}s."));
            }
            else if (Volatile.Read(ref _error) is null)
            {
                var cancelledBy = (SubAgentCancelSource)src;
                Volatile.Write(ref _error, new SubAgentSessionError("Cancelled",
                    $"Subagent '{Profile.Id}' was cancelled by {cancelledBy}."));
            }

            TrySetTerminal(SubAgentStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _error, new SubAgentSessionError("InternalError", ex.Message));
            TrySetTerminal(SubAgentStatus.Failed);
        }
    }

    public void Cancel(SubAgentCancelSource source)
    {
        // First caller wins attribution
        Interlocked.CompareExchange(ref _cancelledBySource, (int)source, -1);
        if (!_cts.IsCancellationRequested)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public SubAgentSessionView Snapshot()
    {
        var rawState = Volatile.Read(ref _terminalState);
        var status = rawState < 0
            ? SubAgentStatus.Running
            : (SubAgentStatus)rawState;

        IReadOnlyList<SubAgentTurnSnapshot> turns;
        lock (_turnsLock) turns = _turns.ToArray();

        return new SubAgentSessionView
        {
            Handle = Handle,
            SubAgentId = Profile.Id,
            Status = status,
            StartedAt = StartedAt,
            ElapsedSeconds = (DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
            Turns = turns,
            Result = status == SubAgentStatus.Completed ? Volatile.Read(ref _finalResult) : null,
            CancelledBy = CancelledBy,
            Error = Volatile.Read(ref _error)
        };
    }

    private bool TrySetTerminal(SubAgentStatus state)
        => Interlocked.CompareExchange(ref _terminalState, (int)state, -1) == -1;

    public async ValueTask DisposeAsync()
    {
        Cancel(SubAgentCancelSource.System);
        if (_agent is not null) await _agent.DisposeAsync();
        _cts.Dispose();
    }
}
