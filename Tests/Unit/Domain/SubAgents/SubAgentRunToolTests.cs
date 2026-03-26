using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.SubAgents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentRunToolTests
{
    private readonly Mock<IAgentFactory> _factory = new();

    private static readonly FeatureConfig TestConfig = new(
        Mock.Of<IToolApprovalHandler>(), ["pattern:*"], "user-1");

    private static readonly SubAgentDefinition TestProfile = new()
    {
        Id = "summarizer",
        Name = "Summarizer",
        Description = "Summarizes content",
        Model = "test-model",
        McpServerEndpoints = []
    };

    private SubAgentRunTool CreateTool(params SubAgentDefinition[] profiles) =>
        new(_factory.Object, new SubAgentRegistryOptions { SubAgents = profiles }, TestConfig);

    [Fact]
    public async Task RunAsync_UnknownProfile_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.RunAsync("unknown", "do something");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("unknown");
    }

    [Fact]
    public async Task RunAsync_ValidProfile_CallsFactoryAndReturnsResult()
    {
        var stubAgent = new StubDisposableAgent("Summary result");
        _factory.Setup(f => f.CreateSubAgent(TestProfile, TestConfig))
            .Returns(stubAgent);

        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "summarize this");

        result["status"]!.GetValue<string>().ShouldBe("completed");
        result["result"]!.GetValue<string>().ShouldBe("Summary result");
        _factory.Verify(f => f.CreateSubAgent(TestProfile, TestConfig), Times.Once);
    }

    [Fact]
    public async Task RunAsync_FactoryThrows_ReturnsError()
    {
        _factory.Setup(f => f.CreateSubAgent(It.IsAny<SubAgentDefinition>(), It.IsAny<FeatureConfig>()))
            .Throws(new InvalidOperationException("factory error"));

        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "do something");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("factory error");
    }

    [Fact]
    public async Task RunAsync_ProfileLookup_IsCaseInsensitive()
    {
        var stubAgent = new StubDisposableAgent("result");
        _factory.Setup(f => f.CreateSubAgent(TestProfile, TestConfig))
            .Returns(stubAgent);

        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("SUMMARIZER", "test");

        result["status"]!.GetValue<string>().ShouldBe("completed");
    }
}

file sealed class StubAgentSession : AgentSession;

file sealed class StubDisposableAgent(string responseText) : DisposableAgent
{
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var update = new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new TextContent(responseText), new UsageContent(new UsageDetails())]
        };
        yield return update;
        await Task.CompletedTask;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new StubAgentSession());

    protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
        AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new StubAgentSession());
}
