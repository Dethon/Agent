using Domain.Contracts;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// The memory anchor is the persisted message count read while the turn is being built, and it
// is correct only because recall runs before the agent is handed the turn. Move the recall call
// after the run and the extraction window would take the current message out of the persisted
// history AND append the fallback copy, handing the extractor the same turn twice with the real
// one labelled as context. This is the only seam that can see that ordering.
public class ChatMonitorMemoryAnchorTests
{
    [Fact]
    public async Task Monitor_RecallHook_IsHandedAPersistedCountExcludingTheTurnBeingBuilt()
    {
        var agent = MonitorTestMocks.CreateAgent();
        // Each run the fake agent accepts is a turn that has been persisted by the time the
        // next one is built, so its count stands in for the persisted message count the
        // anchor is taken from.
        var recall = new RecordingRecallHook(() => agent.ReceivedMessages.Count);
        var channel = MonitorTestMocks.CreateChannel(
            messages:
            [
                MonitorTestMocks.CreateChannelMessage(content: "first"),
                MonitorTestMocks.CreateChannelMessage(content: "second")
            ]);

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            recall,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        recall.Captures.ShouldBe([("first", 0), ("second", 1)]);
    }

    private sealed class RecordingRecallHook(Func<int> persistedCount) : IMemoryRecallHook
    {
        public List<(string Text, int PersistedCount)> Captures { get; } = [];

        public Task EnrichAsync(
            ChatMessage message,
            string userId,
            string? conversationId,
            string? agentId,
            AgentSession thread,
            CancellationToken ct)
        {
            Captures.Add((message.Text, persistedCount()));
            return Task.CompletedTask;
        }
    }
}