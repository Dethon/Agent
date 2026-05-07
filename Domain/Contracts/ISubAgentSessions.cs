using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Contracts;

public interface ISubAgentSessions : IAsyncDisposable
{
    string Start(SubAgentDefinition profile, string prompt, bool silent);
    SubAgentSessionView? Get(string handle);
    IReadOnlyList<SubAgentSessionView> List();
    void Cancel(string handle, SubAgentCancelSource source);
    Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles, SubAgentWaitMode mode,
        TimeSpan timeout, CancellationToken ct);
    bool Release(string handle);
    int ActiveCount { get; }

    void SetParentTurnActive(bool active) { }
    void RebindReply(IChannelConnection channel, string conversationId) { }
}

