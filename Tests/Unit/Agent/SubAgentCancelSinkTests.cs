using Agent.Services.SubAgents;
using Domain.DTOs.SubAgent;
using Shouldly;

namespace Tests.Unit.Agent;

public class SubAgentCancelSinkTests
{
    [Fact]
    public async Task Publish_YieldsRequestOnStream()
    {
        var sink = new SubAgentCancelSink();
        var request = new SubAgentCancelRequest("100:200", "worker-1");

        sink.Publish(request);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var received in sink.Stream.WithCancellation(cts.Token))
        {
            received.ConversationId.ShouldBe("100:200");
            received.Handle.ShouldBe("worker-1");
            break;
        }
    }

    [Fact]
    public async Task Publish_MultipleRequests_AllYieldedInOrder()
    {
        var sink = new SubAgentCancelSink();
        sink.Publish(new SubAgentCancelRequest("conv-1", "handle-a"));
        sink.Publish(new SubAgentCancelRequest("conv-2", "handle-b"));

        var results = new List<SubAgentCancelRequest>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var req in sink.Stream.WithCancellation(cts.Token))
        {
            results.Add(req);
            if (results.Count == 2)
            {
                break;
            }
        }

        results[0].ConversationId.ShouldBe("conv-1");
        results[1].ConversationId.ShouldBe("conv-2");
    }
}
