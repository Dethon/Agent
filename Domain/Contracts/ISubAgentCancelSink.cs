using Domain.DTOs.SubAgent;

namespace Domain.Contracts;

public interface ISubAgentCancelSink
{
    void Publish(SubAgentCancelRequest req);
    IAsyncEnumerable<SubAgentCancelRequest> Stream { get; }
}
