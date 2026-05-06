using System.Runtime.CompilerServices;
using Agent.Services.SubAgents;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Integration.Agents;

[Trait("Category", "Integration")]
public sealed class SubAgentBackgroundFlowTests
{
    [Fact]
    public async Task NaturalCompletion_WhileParentIdle_InjectsSystemMessage()
    {
        var systemCh = new SystemChannelConnection();
        var replyCh = new RecordingChannel();
        var manager = new SubAgentSessionManager(
            agentFactory: _ => new FakeAgent(turns: 1),
            replyToConversationId: "conv-1",
            replyChannel: replyCh,
            systemChannel: systemCh,
            agentKey: new AgentKey("conv-1", "agent-x"));

        manager.SetParentTurnActive(false);
        var handle = manager.Start(Profile(), "go", silent: false);

        var injected = await ReadOneAsync(systemCh.Messages, TimeSpan.FromSeconds(5));
        injected.ShouldNotBeNull();
        injected!.Content.ShouldContain($"handle={handle}");
        injected.Content.ShouldContain("status=completed");
        injected.Content.ShouldContain("subagent_check");
        injected.ConversationId.ShouldBe("conv-1");
        injected.ChannelId.ShouldBe("system");
    }

    [Fact]
    public async Task ParentCancel_DoesNotInjectSystemMessage()
    {
        var systemCh = new SystemChannelConnection();
        var replyCh = new RecordingChannel();
        var manager = new SubAgentSessionManager(
            agentFactory: _ => new FakeAgent(turns: 100, delayPerTurn: TimeSpan.FromMilliseconds(20)),
            replyToConversationId: "conv-1",
            replyChannel: replyCh,
            systemChannel: systemCh,
            agentKey: new AgentKey("conv-1", "agent-x"));

        manager.SetParentTurnActive(false);
        var handle = manager.Start(Profile(), "go", silent: false);
        await Task.Delay(30);
        manager.Cancel(handle, SubAgentCancelSource.Parent);
        await Task.Delay(400); // > 250ms debounce

        var injected = await ReadOneAsync(systemCh.Messages, TimeSpan.FromMilliseconds(200));
        injected.ShouldBeNull();
    }

    [Fact]
    public async Task UserCancel_TerminatesAndInjectsSystemMessage()
    {
        var systemCh = new SystemChannelConnection();
        var replyCh = new RecordingChannel();
        var manager = new SubAgentSessionManager(
            agentFactory: _ => new FakeAgent(turns: 100, delayPerTurn: TimeSpan.FromMilliseconds(20)),
            replyToConversationId: "conv-1",
            replyChannel: replyCh,
            systemChannel: systemCh,
            agentKey: new AgentKey("conv-1", "agent-x"));

        manager.SetParentTurnActive(false);
        var handle = manager.Start(Profile(), "go", silent: false);
        await Task.Delay(30);
        manager.Cancel(handle, SubAgentCancelSource.User);

        var injected = await ReadOneAsync(systemCh.Messages, TimeSpan.FromSeconds(5));
        injected.ShouldNotBeNull();
        injected!.Content.ShouldContain($"handle={handle}");
        injected.Content.ShouldContain("status=cancelled");
        injected.Content.ShouldContain("(cancelled by user)");
    }

    private static SubAgentDefinition Profile() => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = 30
    };

    private static async Task<ChannelMessage?> ReadOneAsync(IAsyncEnumerable<ChannelMessage> stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var msg in stream.WithCancellation(cts.Token))
                return msg;
        }
        catch (OperationCanceledException) { }
        return null;
    }
}

file sealed class RecordingChannel : IChannelConnection
{
    public string ChannelId => "recording";
    public IAsyncEnumerable<ChannelMessage> Messages => AsyncEnumerable.Empty<ChannelMessage>();

    public Task SendReplyAsync(string c, string content, ReplyContentType t, bool isComplete,
        string? msgId, CancellationToken ct) => Task.CompletedTask;

    public Task<ToolApprovalResult> RequestApprovalAsync(string c, IReadOnlyList<ToolApprovalRequest> r,
        CancellationToken ct) => Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(string c, IReadOnlyList<ToolApprovalRequest> r,
        CancellationToken ct) => Task.CompletedTask;

    public Task<string?> CreateConversationAsync(string a, string t, string s,
        CancellationToken ct) => Task.FromResult<string?>(null);
}

file sealed class FakeAgent(int turns, TimeSpan delayPerTurn = default) : DisposableAgent
{
    public override string Name => "FakeAgent";
    public override string Description => "Fake agent for background flow tests";

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

            if (delayPerTurn > TimeSpan.Zero)
                await Task.Delay(delayPerTurn, cancellationToken);

            yield return new AgentResponseUpdate
            {
                Role = ChatRole.Assistant,
                MessageId = $"turn-{i}",
                Contents = [new TextContent(i == turns - 1 ? "done" : $"turn {i}")]
            };
        }
    }

    private sealed class FakeThread : AgentSession;
}
