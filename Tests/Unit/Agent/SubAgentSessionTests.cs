using System.Runtime.CompilerServices;
using Agent.Services.SubAgents;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Agent;

public sealed class SubAgentSessionTests
{
    private static SubAgentDefinition Profile(int maxSec = 60) => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = maxSec
    };

    [Fact]
    public async Task RunAsync_NaturalCompletion_TransitionsToCompleted()
    {
        var fake = new FakeStreamingAgent(turns: 2, finalText: "done");
        var session = new SubAgentSession("h1", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        await session.RunAsync(CancellationToken.None);

        var view = session.Snapshot();
        view.Status.ShouldBe(SubAgentTerminalState.Completed);
        view.Result.ShouldBe("done");
        view.Turns.Count.ShouldBe(2);
        view.CancelledBy.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_CancelMidFlight_TransitionsToCancelled()
    {
        var fake = new FakeStreamingAgent(turns: 5, delayPerTurn: TimeSpan.FromMilliseconds(50));
        var session = new SubAgentSession("h1", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        var runTask = session.RunAsync(CancellationToken.None);
        await Task.Delay(60);
        session.Cancel(SubAgentCancelSource.User);
        await runTask;

        var view = session.Snapshot();
        view.Status.ShouldBe(SubAgentTerminalState.Cancelled);
        view.CancelledBy.ShouldBe(SubAgentCancelSource.User);
        view.Error.ShouldNotBeNull();
        view.Error!.Code.ShouldBe("Cancelled");
    }

    [Fact]
    public async Task RunAsync_TerminalRace_ResolvesToOneState()
    {
        var fake = new FakeStreamingAgent(turns: 1);
        var session = new SubAgentSession("h1", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        var runTask = session.RunAsync(CancellationToken.None);
        Parallel.For(0, 50, _ => session.Cancel(SubAgentCancelSource.Parent));
        await runTask;

        var view = session.Snapshot();
        var validTerminalStates = new[] { SubAgentTerminalState.Completed, SubAgentTerminalState.Cancelled };
        validTerminalStates.ShouldContain(view.Status);
    }

    [Fact]
    public async Task RunAsync_ExceedsMaxExecutionSeconds_TimesOut()
    {
        var fake = new FakeStreamingAgent(turns: 100, delayPerTurn: TimeSpan.FromMilliseconds(20));
        var session = new SubAgentSession("h1", Profile(maxSec: 0), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        await session.RunAsync(CancellationToken.None);

        var view = session.Snapshot();
        view.Status.ShouldBe(SubAgentTerminalState.Cancelled);
        view.CancelledBy.ShouldBe(SubAgentCancelSource.System);
        view.Error.ShouldNotBeNull();
        view.Error!.Code.ShouldBe("Timeout");
    }

    [Fact]
    public async Task Snapshots_TruncateLongArgsAndResults()
    {
        var longArgs = new string('a', 1000);
        var longResult = new string('b', 2000);
        var fake = new FakeStreamingAgent(turns: 1, toolCallArgs: longArgs, toolResultBody: longResult);
        var session = new SubAgentSession("h1", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        await session.RunAsync(CancellationToken.None);

        var view = session.Snapshot();
        view.Turns.ShouldNotBeEmpty();
        var snap = view.Turns[0];
        snap.ToolCalls.ShouldNotBeEmpty();
        snap.ToolResults.ShouldNotBeEmpty();
        snap.ToolCalls[0].ArgsSummary.Length.ShouldBeLessThanOrEqualTo(201); // 200 chars + optional ellipsis char
        snap.ToolResults[0].Summary.Length.ShouldBeLessThanOrEqualTo(501);   // 500 chars + optional ellipsis char
    }
}

file sealed class FakeStreamingAgent(
    int turns,
    string finalText = "ok",
    TimeSpan? delayPerTurn = null,
    string toolCallArgs = "{}",
    string toolResultBody = "ok")
    : DisposableAgent
{
    public override string Name => "FakeStreamingAgent";
    public override string Description => "Fake agent for unit testing";

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeThread());

    protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        System.Text.Json.JsonElement serializedThread,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeThread());

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

            // Assistant text update
            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                MessageId = msgId,
                Contents = [new TextContent(text)]
            };

            // Tool call
            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                MessageId = msgId,
                Contents = [new FunctionCallContent($"call-{i}", "fake_tool",
                    new Dictionary<string, object?> { ["args"] = toolCallArgs })]
            };

            // Tool result (role = Tool)
            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Tool,
                MessageId = msgId,
                Contents = [new FunctionResultContent($"call-{i}", toolResultBody)]
            };
        }
    }

    private sealed class FakeThread : AgentSession;
}
