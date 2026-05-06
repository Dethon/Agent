using Domain.Agents;
using Domain.DTOs;
using Domain.Tools.SubAgents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentRunToolBackgroundTests
{
    private static SubAgentDefinition Profile() => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = 60
    };

    [Fact]
    public async Task RunAsync_WithBackgroundFlag_StartsAndReturnsHandle()
    {
        var sessions = new FakeSubAgentSessions { StartFunc = (_, _, _) => "handle-42" };
        var config = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentRunTool(
            new SubAgentRegistryOptions { SubAgents = [Profile()] }, config);

        var result = await tool.RunAsync("researcher", "do research", run_in_background: true, silent: false);

        result["status"]!.ToString().ShouldBe("started");
        result["handle"]!.ToString().ShouldBe("handle-42");
        result["subagent_id"]!.ToString().ShouldBe("researcher");
        sessions.StartCallCount.ShouldBe(1);
        sessions.LastStartSilent.ShouldBe(false);
    }

    [Fact]
    public async Task RunAsync_WithBackgroundFlag_PassesSilentTrueToSessions()
    {
        var sessions = new FakeSubAgentSessions { StartFunc = (_, _, _) => "h-42" };
        var registry = new SubAgentRegistryOptions { SubAgents = [Profile()] };
        var cfg = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentRunTool(registry, cfg);

        await tool.RunAsync("researcher", "go", run_in_background: true, silent: true);

        sessions.LastStartSilent.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_WithBackgroundFlag_NoSessions_ReturnsUnavailable()
    {
        var config = new FeatureConfig(SubAgentSessions: null);
        var tool = new SubAgentRunTool(
            new SubAgentRegistryOptions { SubAgents = [Profile()] }, config);

        var result = await tool.RunAsync("researcher", "do research", run_in_background: true);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("unavailable");
    }

    [Fact]
    public async Task RunAsync_WithoutBackgroundFlag_PreservesExistingBehavior()
    {
        var sessions = new FakeSubAgentSessions { StartFunc = (_, _, _) => "handle-99" };
        var config = new FeatureConfig(
            SubAgentFactory: _ => new FakeBlockingAgent(),
            SubAgentSessions: sessions);
        var tool = new SubAgentRunTool(
            new SubAgentRegistryOptions { SubAgents = [Profile()] }, config);

        var result = await tool.RunAsync("researcher", "do research", run_in_background: false);

        result["status"]!.ToString().ShouldBe("completed");
        result["result"]!.ToString().ShouldBe("ok");
        sessions.StartCallCount.ShouldBe(0);
    }
}

file sealed class FakeBlockingAgentSession : AgentSession;

file sealed class FakeBlockingAgent : DisposableAgent
{
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "ok")));

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeBlockingAgentSession());

    protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
        AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new FakeBlockingAgentSession());
}
