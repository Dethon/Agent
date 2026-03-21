using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpAgentDeserializationTests : IAsyncDisposable
{
    private readonly McpAgent _agent;

    public McpAgentDeserializationTests()
    {
        var chatClient = new Mock<IChatClient>();
        var stateStore = new Mock<IThreadStateStore>();
        _agent = new McpAgent(
            [],
            chatClient.Object,
            "test-agent",
            "",
            stateStore.Object,
            "test-user");
    }

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
    }

    [Fact]
    public async Task DeserializeSession_WithAgentKeyString_PutsKeyInStateBag()
    {
        var agentKey = new AgentKey(123, 456, "test-agent");
        var serialized = JsonSerializer.SerializeToElement(agentKey.ToString());

        var session = await _agent.DeserializeSessionAsync(serialized);

        session.StateBag.TryGetValue<string>(RedisChatMessageStore.StateKey, out var key).ShouldBeTrue();
        key.ShouldBe(agentKey.ToString());
    }

    [Fact]
    public async Task DeserializeSession_WithProperlySerializedSession_PreservesStateBag()
    {
        // First, create and serialize a session to get the proper format
        var agentKey = new AgentKey(789, 101, "test-agent");
        var serialized = JsonSerializer.SerializeToElement(agentKey.ToString());
        var session = await _agent.DeserializeSessionAsync(serialized);

        // Serialize the session (this produces the new format with stateBag)
        var reSerialized = await _agent.SerializeSessionAsync(session);

        // Deserialize from the new format
        var restored = await _agent.DeserializeSessionAsync(reSerialized);

        restored.StateBag.TryGetValue<string>(RedisChatMessageStore.StateKey, out var key).ShouldBeTrue();
        key.ShouldBe(agentKey.ToString());
    }

    [Fact]
    public async Task DeserializeSession_AgentKeyString_MatchesChatHubLookupKey()
    {
        // ChatHub.GetHistory uses agentKey.ToString() to look up messages in Redis.
        // The agent must use the same key when reading/writing messages.
        var agentKey = new AgentKey(123, 456, "test-agent");
        var serialized = JsonSerializer.SerializeToElement(agentKey.ToString());

        var session = await _agent.DeserializeSessionAsync(serialized);

        session.StateBag.TryGetValue<string>(RedisChatMessageStore.StateKey, out var redisKey).ShouldBeTrue();
        redisKey.ShouldBe(agentKey.ToString());
    }
}
