using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSessionManager : ISubAgentSessions, IAsyncDisposable
{
    public const int MaxConcurrentPerThread = 8;

    public event Action<IReadOnlyList<string>>? WakeRequested;

    private readonly Func<SubAgentDefinition, DisposableAgent> _agentFactory;
    private readonly TimeSpan _wakeDebounce;
    private readonly ConcurrentDictionary<string, SubAgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Task> _runs = new();
    private readonly SystemChannelConnection? _systemChannel;
    private readonly AgentKey _agentKey;

    private readonly object _wakeLock = new();
    private readonly HashSet<string> _wakeBuffer = [];
    private CancellationTokenSource? _wakeDebounceCts;
    private bool _isParentTurnActive = true;

    private volatile string _replyToConversationId;
    private volatile IChannelConnection? _replyChannel;

    public SubAgentSessionManager(
        Func<SubAgentDefinition, DisposableAgent> agentFactory,
        string replyToConversationId,
        IChannelConnection? replyChannel,
        TimeSpan? wakeDebounce = null,
        SystemChannelConnection? systemChannel = null,
        AgentKey agentKey = default)
    {
        _agentFactory = agentFactory;
        _replyToConversationId = replyToConversationId;
        _replyChannel = replyChannel;
        _wakeDebounce = wakeDebounce ?? TimeSpan.FromMilliseconds(250);
        _systemChannel = systemChannel;
        _agentKey = agentKey;
    }

    public void RebindReply(IChannelConnection channel, string conversationId)
    {
        _replyChannel = channel;
        _replyToConversationId = conversationId;
    }

    public int ActiveCount => _sessions.Values.Count(s => !s.IsTerminal);

    public string Start(SubAgentDefinition profile, string prompt, bool silent)
    {
        if (ActiveCount >= MaxConcurrentPerThread)
            throw new InvalidOperationException(
                $"Too many active subagents in this thread ({MaxConcurrentPerThread} max). Cancel or wait on existing handles first.");

        var handle = NewHandle();
        var session = new SubAgentSession(handle, profile, prompt, silent,
            agentFactory: () => _agentFactory(profile),
            replyToConversationId: _replyToConversationId,
            replyChannel: _replyChannel);
        _sessions[handle] = session;

        var run = Task.Run(async () =>
        {
            try { await session.RunAsync(CancellationToken.None); }
            finally { OnSessionTerminal(session); }
        });
        _runs[handle] = run;

        return handle;
    }

    public SubAgentSessionView? Get(string handle) => _sessions.TryGetValue(handle, out var s) ? s.Snapshot() : null;

    public IReadOnlyList<SubAgentSessionView> List() => _sessions.Values.Select(s => s.Snapshot()).ToArray();

    public void Cancel(string handle, SubAgentCancelSource source)
    {
        if (_sessions.TryGetValue(handle, out var s)) s.Cancel(source);
    }

    public async Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles,
        SubAgentWaitMode mode, TimeSpan timeout, CancellationToken ct)
    {
        var tasks = handles
            .Where(_runs.ContainsKey)
            .Select(h => (h, t: _runs[h]))
            .ToArray();
        if (tasks.Length == 0) return new SubAgentWaitResult([], handles);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (mode == SubAgentWaitMode.All)
        {
            try { await Task.WhenAll(tasks.Select(x => x.t)).WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) { }
        }
        else
        {
            try { await Task.WhenAny(tasks.Select(x => x.t)).WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) { }
        }

        var completed = tasks.Where(x => x.t.IsCompleted).Select(x => x.h).ToArray();
        var still = tasks.Where(x => !x.t.IsCompleted).Select(x => x.h).ToArray();
        return new SubAgentWaitResult(completed, still);
    }

    public bool Release(string handle)
    {
        if (!_sessions.TryGetValue(handle, out var s))
            return false;
        if (!s.IsTerminal)
            throw new InvalidOperationException($"Cannot release running session '{handle}'.");
        _sessions.TryRemove(handle, out _);
        _runs.TryRemove(handle, out _);
        // Session is terminal → DisposeAsync only releases _cts and the agent; safe to fire-and-forget.
        _ = s.DisposeAsync().AsTask();
        return true;
    }

    public void SetParentTurnActive(bool active)
    {
        bool flushNow;
        lock (_wakeLock)
        {
            _isParentTurnActive = active;
            flushNow = !active && _wakeBuffer.Count > 0;
        }
        if (flushNow) FlushWakeNow();
    }

    private void OnSessionTerminal(SubAgentSession session)
    {
        // Skip wake if parent cancelled itself.
        if (session.CancelledBy == SubAgentCancelSource.Parent) return;

        lock (_wakeLock)
        {
            _wakeBuffer.Add(session.Handle);
            // Restart debounce window: capture old, install new, cancel+dispose old.
            var oldCts = _wakeDebounceCts;
            _wakeDebounceCts = new CancellationTokenSource();
            oldCts?.Cancel();
            oldCts?.Dispose();
            // Schedule the debounce
            _ = ScheduleWakeFlushAsync(_wakeDebounceCts.Token);
        }
    }

    private async Task ScheduleWakeFlushAsync(CancellationToken token)
    {
        try { await Task.Delay(_wakeDebounce, token); }
        catch (OperationCanceledException) { return; }
        FlushWakeNow();
    }

    private void FlushWakeNow()
    {
        string[] toEmit;
        lock (_wakeLock)
        {
            if (_isParentTurnActive) return;
            if (_wakeBuffer.Count == 0) return;
            toEmit = _wakeBuffer.ToArray();
            _wakeBuffer.Clear();
        }
        WakeRequested?.Invoke(toEmit);
        _ = EmitWakeAsync(toEmit);
    }

    private Task EmitWakeAsync(IReadOnlyList<string> handles)
    {
        if (_systemChannel is null || _replyChannel is null) return Task.CompletedTask;

        var handleList = string.Join(", ", handles);
        var content = $"[system] subagent_check: Background subagent(s) completed: {handleList}. " +
                      $"Call subagent_check with the handle(s) to review results.";

        var msg = new ChannelMessage
        {
            ChannelId = SystemChannelConnection.Id,
            ConversationId = _replyToConversationId,
            Sender = "system",
            Content = content,
            AgentId = _agentKey.AgentId
        };

        _systemChannel.InjectAsync(msg, _replyChannel);
        return Task.CompletedTask;
    }

    private static string NewHandle() => Guid.NewGuid().ToString("N")[..16];

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
            s.Cancel(SubAgentCancelSource.System);
        try { await Task.WhenAll(_runs.Values).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        foreach (var s in _sessions.Values)
            await s.DisposeAsync();
        _sessions.Clear();
        _runs.Clear();
        _wakeDebounceCts?.Cancel();
        _wakeDebounceCts?.Dispose();
        _wakeDebounceCts = null;
    }
}
