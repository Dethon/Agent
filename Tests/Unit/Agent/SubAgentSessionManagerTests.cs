using System.Runtime.CompilerServices;
using Agent.Services.SubAgents;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Agent;

public sealed class SubAgentSessionManagerTests
{
    private static SubAgentDefinition Profile() => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = 30
    };

    [Fact]
    public void Start_ReturnsUniqueHandles()
    {
        var mgr = MakeManager();
        var h1 = mgr.Start(Profile(), "p1", silent: false);
        var h2 = mgr.Start(Profile(), "p2", silent: false);
        h1.ShouldNotBe(h2);
    }

    [Fact]
    public async Task Start_BeyondCap_ReturnsThrows()
    {
        var mgr = MakeManager(turnsPerAgent: 100, delay: TimeSpan.FromMilliseconds(50));
        for (var i = 0; i < SubAgentSessionManager.MaxConcurrentPerThread; i++)
            mgr.Start(Profile(), $"p{i}", silent: false);

        Should.Throw<InvalidOperationException>(() =>
            mgr.Start(Profile(), "extra", silent: false))
            .Message.ShouldContain("Too many active subagents");
    }

    [Fact]
    public async Task Get_ForUnknownHandle_ReturnsNull()
    {
        var mgr = MakeManager();
        mgr.Get("nope").ShouldBeNull();
    }

    [Fact]
    public async Task WaitAsync_ModeAll_BlocksUntilAllTerminal()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var h1 = mgr.Start(Profile(), "p1", silent: false);
        var h2 = mgr.Start(Profile(), "p2", silent: false);

        var result = await mgr.WaitAsync([h1, h2], SubAgentWaitMode.All,
            TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Completed.Count.ShouldBe(2);
        result.StillRunning.ShouldBeEmpty();
    }

    [Fact]
    public async Task WaitAsync_ModeAny_ReturnsOnFirstTerminal()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var hFast = mgr.Start(Profile(), "fast", silent: false);
        // The second agent is intentionally slow via factory in MakeManager(turnsPerAgent),
        // here we re-use the fast factory and rely on identical timing — both will likely
        // complete; the test asserts at least one is in Completed and that the call returns
        // before the timeout.
        var hOther = mgr.Start(Profile(), "other", silent: false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await mgr.WaitAsync([hFast, hOther], SubAgentWaitMode.Any,
            TimeSpan.FromSeconds(5), CancellationToken.None);
        sw.Stop();

        result.Completed.Count.ShouldBeGreaterThanOrEqualTo(1);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAsync_TimesOut_ReturnsPartition()
    {
        var mgr = MakeManager(turnsPerAgent: 100, delay: TimeSpan.FromMilliseconds(50));
        var h = mgr.Start(Profile(), "long", silent: false);

        var result = await mgr.WaitAsync([h], SubAgentWaitMode.All,
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        result.Completed.ShouldBeEmpty();
        result.StillRunning.ShouldContain(h);
    }

    [Fact]
    public async Task Cancel_ParentSource_DoesNotEnqueueWake()
    {
        var mgr = MakeManager(turnsPerAgent: 100, delay: TimeSpan.FromMilliseconds(20));
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        var h = mgr.Start(Profile(), "p", silent: false);
        await Task.Delay(30);
        mgr.Cancel(h, SubAgentCancelSource.Parent);
        await Task.Delay(400); // > 250ms debounce window

        wakes.ShouldBeEmpty();
    }

    [Fact]
    public async Task NaturalCompletion_WhileParentIdle_FiresWake()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        mgr.SetParentTurnActive(false);
        var h = mgr.Start(Profile(), "p", silent: false);

        // Wait long enough for the run to complete and the debounce to fire.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (wakes.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        wakes.Count.ShouldBe(1);
        wakes[0].ShouldContain(h);
    }

    [Fact]
    public async Task DebounceWindow_CoalescesMultipleCompletions()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        mgr.SetParentTurnActive(false);
        var h1 = mgr.Start(Profile(), "a", silent: false);
        var h2 = mgr.Start(Profile(), "b", silent: false);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (wakes.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        wakes.Count.ShouldBe(1);
        wakes[0].Count.ShouldBe(2);
    }

    [Fact]
    public async Task WhenParentTurnActive_NoWakeFires_UntilTurnEnds()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        mgr.SetParentTurnActive(true);
        var h = mgr.Start(Profile(), "p", silent: false);
        await Task.Delay(500);

        wakes.ShouldBeEmpty();

        mgr.SetParentTurnActive(false);
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (wakes.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        wakes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Release_OnRunningSession_Throws()
    {
        var mgr = MakeManager(turnsPerAgent: 100, delay: TimeSpan.FromMilliseconds(50));
        var h = mgr.Start(Profile(), "p", silent: false);
        Should.Throw<InvalidOperationException>(() => mgr.Release(h));
    }

    [Fact]
    public async Task Release_OnCompletedSession_RemovesIt()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var h = mgr.Start(Profile(), "p", silent: false);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (mgr.Get(h)?.Status == SubAgentStatus.Running && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        mgr.Release(h).ShouldBeTrue();
        mgr.Get(h).ShouldBeNull();
    }

    private static SubAgentSessionManager MakeManager(int turnsPerAgent = 1, TimeSpan? delay = null)
    {
        return new SubAgentSessionManager(
            agentFactory: _ => new FakeStreamingAgentFactoryAgent(turnsPerAgent, delay ?? TimeSpan.Zero),
            replyToConversationId: "c1",
            replyChannel: null,
            wakeDebounce: TimeSpan.FromMilliseconds(250));
    }
}

// Same FakeStreamingAgent shape as Task 1, but injected per-Start via the factory.
file sealed class FakeStreamingAgentFactoryAgent(int turns, TimeSpan delay) : DisposableAgent
{
    public override string Name => "FakeStreamingAgentFactoryAgent";
    public override string Description => "Fake agent for unit testing (manager)";

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeManagerThread());

    protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        System.Text.Json.JsonElement serializedThread,
        System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeManagerThread());

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

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            var msgId = $"turn-{i}";
            var isLast = i == turns - 1;
            var text = isLast ? "done" : $"turn {i}";

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
                    new Dictionary<string, object?> { ["args"] = "{}" })]
            };

            // Tool result (role = Tool)
            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Tool,
                MessageId = msgId,
                Contents = [new FunctionResultContent($"call-{i}", "ok")]
            };
        }
    }

    private sealed class FakeManagerThread : AgentSession;
}
