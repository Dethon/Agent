using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class ChatHubCustomAgentIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private readonly string _userId = $"int-user-{Guid.NewGuid():N}";
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", _userId);
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task FullFlow_RegisterGetUnregisterGet()
    {
        // Arrange
        var registration = new CustomAgentRegistration
        {
            Name = "IntBot",
            Model = "gpt-4",
            McpServerEndpoints = []
        };

        // Act - Register
        var registered = await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);
        registered.ShouldNotBeNull();
        registered.Id.ShouldStartWith("custom-");
        registered.Name.ShouldBe("IntBot");

        // Act - First GetAgents (should include IntBot)
        var agentsAfterRegister = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");
        agentsAfterRegister.ShouldNotBeNull();
        agentsAfterRegister.ShouldContain(a => a.Name == "IntBot" && a.Id.StartsWith("custom-"));
        agentsAfterRegister.Count.ShouldBe(3); // 2 built-in + 1 custom

        // Act - Unregister
        var unregistered = await _connection.InvokeAsync<bool>("UnregisterCustomAgent", registered.Id);
        unregistered.ShouldBeTrue();

        // Act - Second GetAgents (should exclude IntBot)
        var agentsAfterUnregister = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");
        agentsAfterUnregister.ShouldNotBeNull();
        agentsAfterUnregister.ShouldNotContain(a => a.Name == "IntBot");
        agentsAfterUnregister.Count.ShouldBe(2); // only built-in
    }

    [Fact]
    public async Task TwoUsers_CustomAgentsIsolated()
    {
        // Arrange - Alice registers a custom agent
        var aliceRegistration = new CustomAgentRegistration
        {
            Name = "AliceBot",
            Model = "gpt-4",
            McpServerEndpoints = []
        };
        await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", aliceRegistration);

        // Arrange - Bob connects and registers a custom agent
        var bobConnection = fixture.CreateHubConnection();
        await bobConnection.StartAsync();

        try
        {
            await bobConnection.InvokeAsync("RegisterUser", $"bob-{Guid.NewGuid():N}");
            var bobRegistration = new CustomAgentRegistration
            {
                Name = "BobBot",
                Model = "gpt-4",
                McpServerEndpoints = []
            };
            await bobConnection.InvokeAsync<AgentInfo>("RegisterCustomAgent", bobRegistration);

            // Act
            var aliceAgents = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");
            var bobAgents = await bobConnection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

            // Assert - Alice sees AliceBot but not BobBot
            aliceAgents.ShouldContain(a => a.Name == "AliceBot");
            aliceAgents.ShouldNotContain(a => a.Name == "BobBot");

            // Assert - Bob sees BobBot but not AliceBot
            bobAgents.ShouldContain(a => a.Name == "BobBot");
            bobAgents.ShouldNotContain(a => a.Name == "AliceBot");

            // Assert - Both see all built-in agents
            aliceAgents.ShouldContain(a => a.Id == "test-agent");
            aliceAgents.ShouldContain(a => a.Id == "second-agent");
            bobAgents.ShouldContain(a => a.Id == "test-agent");
            bobAgents.ShouldContain(a => a.Id == "second-agent");
        }
        finally
        {
            await bobConnection.DisposeAsync();
        }
    }

    [Fact]
    public async Task RegisterMultiple_AllVisibleInGetAgents()
    {
        // Arrange & Act - Register 3 agents
        var agents = new List<AgentInfo>();
        foreach (var name in new[] { "A", "B", "C" })
        {
            var registration = new CustomAgentRegistration
            {
                Name = name,
                Model = "gpt-4",
                McpServerEndpoints = []
            };
            var info = await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);
            agents.Add(info);
        }

        // Act - GetAgents
        var allAgents = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

        // Assert - All 3 custom agents present
        allAgents.ShouldNotBeNull();
        allAgents.Count.ShouldBe(5); // 2 built-in + 3 custom
        allAgents.ShouldContain(a => a.Name == "A");
        allAgents.ShouldContain(a => a.Name == "B");
        allAgents.ShouldContain(a => a.Name == "C");

        // Assert - All have distinct custom- IDs
        var customIds = agents.Select(a => a.Id).ToList();
        customIds.ShouldAllBe(id => id.StartsWith("custom-"));
        customIds.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task UnregisterOne_OthersRemain()
    {
        // Arrange - Register 2 agents
        var reg1 = new CustomAgentRegistration
        {
            Name = "First",
            Model = "gpt-4",
            McpServerEndpoints = []
        };
        var reg2 = new CustomAgentRegistration
        {
            Name = "Second",
            Model = "gpt-4",
            McpServerEndpoints = []
        };
        var agent1 = await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", reg1);
        var agent2 = await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", reg2);

        // Act - Unregister first
        var removed = await _connection.InvokeAsync<bool>("UnregisterCustomAgent", agent1.Id);
        removed.ShouldBeTrue();

        // Act - GetAgents
        var agents = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

        // Assert - Second agent still present, first gone
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(3); // 2 built-in + 1 remaining custom
        agents.ShouldContain(a => a.Id == agent2.Id && a.Name == "Second");
        agents.ShouldNotContain(a => a.Id == agent1.Id);
    }
}
