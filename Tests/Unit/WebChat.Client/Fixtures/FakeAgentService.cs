using Domain.DTOs.Channel;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeAgentService(CallRecorder? recorder = null) : IAgentService
{
    public IReadOnlyList<AgentCatalogEntry> Agents { get; set; } = [];

    public Exception? ThrowOnGetAgents { get; set; }

    // Set to answer not live for every call, the way a transport between connections does.
    public bool NotLive { get; set; }

    // Set to hold every fetch open, so a test can decide what else happens while one is in flight.
    public TaskCompletionSource? Gate { get; set; }

    public async Task<HubResult<IReadOnlyList<AgentCatalogEntry>>> GetAgentsAsync()
    {
        recorder?.Record("agents");

        if (ThrowOnGetAgents is not null)
        {
            throw ThrowOnGetAgents;
        }

        if (Gate is not null)
        {
            await Gate.Task;
        }

        return NotLive
            ? HubResult<IReadOnlyList<AgentCatalogEntry>>.NotLive
            : HubResult<IReadOnlyList<AgentCatalogEntry>>.Answered(Agents);
    }
}