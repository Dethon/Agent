using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Tests.Unit.Domain.SubAgents;

internal sealed class FakeSubAgentSessions : ISubAgentSessions
{
    public Func<SubAgentDefinition, string, bool, string> StartFunc { get; set; }
        = (_, _, _) => "h-1";
    public Func<string, SubAgentSessionView?> GetFunc { get; set; } = _ => null;
    public Func<IReadOnlyList<SubAgentSessionView>> ListFunc { get; set; } = () => [];
    public Func<string, bool> ReleaseFunc { get; set; } = _ => false;
    public Func<IReadOnlyList<string>, SubAgentWaitMode, TimeSpan, CancellationToken, Task<SubAgentWaitResult>> WaitFunc { get; set; }
        = (handles, _, _, _) => Task.FromResult(new SubAgentWaitResult([], handles));
    public int ActiveCount { get; set; }

    public int StartCallCount { get; private set; }
    public string? LastStartPrompt { get; private set; }
    public bool LastStartSilent { get; private set; }

    public int CancelCallCount { get; private set; }
    public string? LastCancelHandle { get; private set; }
    public SubAgentCancelSource? LastCancelSource { get; private set; }

    public string Start(SubAgentDefinition profile, string prompt, bool silent)
    {
        StartCallCount++;
        LastStartPrompt = prompt;
        LastStartSilent = silent;
        return StartFunc(profile, prompt, silent);
    }

    public SubAgentSessionView? Get(string handle) => GetFunc(handle);
    public IReadOnlyList<SubAgentSessionView> List() => ListFunc();
    public bool Release(string handle) => ReleaseFunc(handle);
    public Task<SubAgentWaitResult> WaitAsync(
        IReadOnlyList<string> handles, SubAgentWaitMode mode, TimeSpan timeout, CancellationToken ct)
        => WaitFunc(handles, mode, timeout, ct);

    public void Cancel(string handle, SubAgentCancelSource source)
    {
        CancelCallCount++;
        LastCancelHandle = handle;
        LastCancelSource = source;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

