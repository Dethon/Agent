using System.Runtime.CompilerServices;
using Agent.Services.SubAgents;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.SubAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Agent;

public sealed class SubAgentMetricsTests
{
    private static SubAgentDefinition Profile(int maxSec = 60) => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = maxSec
    };

    [Fact]
    public async Task RunAsync_NaturalCompletion_EmitsSnapshotAndTerminalEvents()
    {
        var publisher = new FakeMetricsPublisher();
        var fake = new FakeMetricsStreamingAgent(turns: 2, finalText: "done");
        var session = new SubAgentSession("h1", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1",
            metricsPublisher: publisher);

        await session.RunAsync(CancellationToken.None);

        // Two snapshot events (one per turn)
        var snapshots = publisher.Events.OfType<SubAgentSnapshotAppendedEvent>().ToList();
        snapshots.Count.ShouldBe(2);
        snapshots.Select(s => s.TurnIndex).ShouldBe([0, 1], ignoreOrder: false);
        snapshots.ShouldAllBe(s => s.Handle == "h1");
        snapshots.ShouldAllBe(s => s.SubAgentId == "researcher");

        // One terminal event with "completed" state
        var terminals = publisher.Events.OfType<SubAgentSessionTerminalEvent>().ToList();
        terminals.Count.ShouldBe(1);
        terminals[0].TerminalState.ShouldBe("completed");
        terminals[0].CancelledBy.ShouldBeNull();
        terminals[0].ElapsedSeconds.ShouldBeGreaterThan(0);
        terminals[0].Handle.ShouldBe("h1");
        terminals[0].SubAgentId.ShouldBe("researcher");
    }

    [Fact]
    public async Task RunAsync_Cancelled_EmitsTerminalEventWithCancelledBy()
    {
        var publisher = new FakeMetricsPublisher();
        var fake = new FakeMetricsStreamingAgent(turns: 50, delayPerTurn: TimeSpan.FromMilliseconds(50));
        var session = new SubAgentSession("h2", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1",
            metricsPublisher: publisher);

        var runTask = session.RunAsync(CancellationToken.None);
        await Task.Delay(60);
        session.Cancel(SubAgentCancelSource.Parent);
        await runTask;

        var terminals = publisher.Events.OfType<SubAgentSessionTerminalEvent>().ToList();
        terminals.Count.ShouldBe(1);
        terminals[0].TerminalState.ShouldBe("cancelled");
        terminals[0].CancelledBy.ShouldBe("parent");
        terminals[0].Handle.ShouldBe("h2");
    }
}

file sealed class FakeMetricsPublisher : IMetricsPublisher
{
    private readonly List<MetricEvent> _events = [];

    public IReadOnlyList<MetricEvent> Events
    {
        get { lock (_events) return _events.ToArray(); }
    }

    public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
    {
        lock (_events) _events.Add(metricEvent);
        return Task.CompletedTask;
    }
}

file sealed class FakeMetricsStreamingAgent(
    int turns,
    string finalText = "ok",
    TimeSpan? delayPerTurn = null)
    : DisposableAgent
{
    public override string Name => "FakeMetricsStreamingAgent";
    public override string Description => "Fake agent for metrics unit testing";

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeMetricsThread());

    protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        System.Text.Json.JsonElement serializedThread,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeMetricsThread());

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentResponse());

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < turns; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (delayPerTurn.HasValue)
                await Task.Delay(delayPerTurn.Value, cancellationToken);

            var msgId = $"turn-{i}";
            var isLast = i == turns - 1;
            var text = isLast ? finalText : $"turn {i}";

            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                MessageId = msgId,
                Contents = [new TextContent(text)]
            };

            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                MessageId = msgId,
                Contents = [new FunctionCallContent($"call-{i}", "fake_tool",
                    new Dictionary<string, object?> { ["args"] = "{}" })]
            };

            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Tool,
                MessageId = msgId,
                Contents = [new FunctionResultContent($"call-{i}", "ok")]
            };
        }
    }

    private sealed class FakeMetricsThread : AgentSession;
}
