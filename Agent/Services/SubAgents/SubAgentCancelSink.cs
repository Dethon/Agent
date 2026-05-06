using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.SubAgent;

namespace Agent.Services.SubAgents;

public sealed class SubAgentCancelSink : ISubAgentCancelSink
{
    private readonly Channel<SubAgentCancelRequest> _channel =
        Channel.CreateUnbounded<SubAgentCancelRequest>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });

    public void Publish(SubAgentCancelRequest req) => _channel.Writer.TryWrite(req);

    public IAsyncEnumerable<SubAgentCancelRequest> Stream => _channel.Reader.ReadAllAsync();
}
