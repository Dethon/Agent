using Domain.DTOs.Channel;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeAgentService(CallRecorder? recorder = null) : IAgentService
{
    public IReadOnlyList<AgentCatalogEntry> Agents { get; set; } = [];

    public Exception? ThrowOnGetAgents { get; set; }

    public Task<IReadOnlyList<AgentCatalogEntry>> GetAgentsAsync()
    {
        recorder?.Record("agents");

        return ThrowOnGetAgents is null
            ? Task.FromResult(Agents)
            : Task.FromException<IReadOnlyList<AgentCatalogEntry>>(ThrowOnGetAgents);
    }
}