using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class ChatHubCustomAgentTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", "test-user");
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RegisterCustomAgent_ValidRegistration_ReturnsAgentInfo()
    {
        // Arrange
        var registration = new CustomAgentRegistration
        {
            Name = "MyBot",
            Model = "gpt-4",
            McpServerEndpoints = [],
            Description = "My bot"
        };

        // Act
        var result = await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldStartWith("custom-");
        result.Name.ShouldBe("MyBot");
        result.Description.ShouldBe("My bot");
    }

    [Fact]
    public async Task RegisterCustomAgent_EmptyName_ThrowsHubException()
    {
        // Arrange
        var registration = new CustomAgentRegistration
        {
            Name = "",
            Model = "gpt-4",
            McpServerEndpoints = []
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<HubException>(
            () => _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration));
        ex.Message.ShouldContain("Name");
    }

    [Fact]
    public async Task RegisterCustomAgent_WhitespaceName_ThrowsHubException()
    {
        // Arrange
        var registration = new CustomAgentRegistration
        {
            Name = "   ",
            Model = "gpt-4",
            McpServerEndpoints = []
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<HubException>(
            () => _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration));
        ex.Message.ShouldContain("Name");
    }

    [Fact]
    public async Task RegisterCustomAgent_EmptyModel_ThrowsHubException()
    {
        // Arrange
        var registration = new CustomAgentRegistration
        {
            Name = "MyBot",
            Model = "",
            McpServerEndpoints = []
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<HubException>(
            () => _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration));
        ex.Message.ShouldContain("Model");
    }

    [Fact]
    public async Task RegisterCustomAgent_WhitespaceModel_ThrowsHubException()
    {
        // Arrange
        var registration = new CustomAgentRegistration
        {
            Name = "MyBot",
            Model = "   ",
            McpServerEndpoints = []
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<HubException>(
            () => _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration));
        ex.Message.ShouldContain("Model");
    }

    [Fact]
    public async Task RegisterCustomAgent_WithoutRegisteredUser_ThrowsHubException()
    {
        // Arrange - create a fresh connection without calling RegisterUser
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        try
        {
            var registration = new CustomAgentRegistration
            {
                Name = "MyBot",
                Model = "gpt-4",
                McpServerEndpoints = []
            };

            // Act & Assert
            var ex = await Should.ThrowAsync<HubException>(
                () => unregisteredConnection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration));
            ex.Message.ShouldContain("not registered");
        }
        finally
        {
            await unregisteredConnection.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnregisterCustomAgent_ExistingAgent_ReturnsTrue()
    {
        // Arrange - register an agent first
        var registration = new CustomAgentRegistration
        {
            Name = "AgentToRemove",
            Model = "gpt-4",
            McpServerEndpoints = []
        };
        var agentInfo = await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);

        // Act
        var result = await _connection.InvokeAsync<bool>("UnregisterCustomAgent", agentInfo.Id);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task UnregisterCustomAgent_NonExistent_ReturnsFalse()
    {
        // Act
        var result = await _connection.InvokeAsync<bool>("UnregisterCustomAgent", "custom-nonexistent");

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task UnregisterCustomAgent_WithoutRegisteredUser_ThrowsHubException()
    {
        // Arrange - create a fresh connection without calling RegisterUser
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        try
        {
            // Act & Assert
            var ex = await Should.ThrowAsync<HubException>(
                () => unregisteredConnection.InvokeAsync<bool>("UnregisterCustomAgent", "some-id"));
            ex.Message.ShouldContain("not registered");
        }
        finally
        {
            await unregisteredConnection.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetAgents_AfterRegisteringCustomAgent_IncludesIt()
    {
        // Arrange - register a custom agent
        var registration = new CustomAgentRegistration
        {
            Name = "MyBot",
            Model = "gpt-4",
            McpServerEndpoints = []
        };
        await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);

        // Act
        var agents = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

        // Assert
        agents.ShouldNotBeNull();
        agents.ShouldContain(a => a.Name == "MyBot" && a.Id.StartsWith("custom-"));
        // Built-in agents (test-agent, second-agent) + 1 custom
        agents.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetAgents_AfterUnregistering_ExcludesCustomAgent()
    {
        // Arrange - register then unregister
        var registration = new CustomAgentRegistration
        {
            Name = "TempBot",
            Model = "gpt-4",
            McpServerEndpoints = []
        };
        var agentInfo = await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);
        await _connection.InvokeAsync<bool>("UnregisterCustomAgent", agentInfo.Id);

        // Act
        var agents = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

        // Assert
        agents.ShouldNotBeNull();
        agents.ShouldNotContain(a => a.Id == agentInfo.Id);
        // Only built-in agents remain
        agents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAgents_CustomAgentsIsolatedPerUser()
    {
        // Arrange - user1 registers a custom agent via the default connection
        var registration = new CustomAgentRegistration
        {
            Name = "User1Bot",
            Model = "gpt-4",
            McpServerEndpoints = []
        };
        await _connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);

        // Create a second connection for user2
        var connection2 = fixture.CreateHubConnection();
        await connection2.StartAsync();

        try
        {
            await connection2.InvokeAsync("RegisterUser", "user2");

            // Act - user2's GetAgents should NOT contain User1Bot
            var user2Agents = await connection2.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

            // user1's GetAgents should contain User1Bot
            var user1Agents = await _connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

            // Assert
            user2Agents.ShouldNotContain(a => a.Name == "User1Bot");
            user1Agents.ShouldContain(a => a.Name == "User1Bot");
        }
        finally
        {
            await connection2.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnregisterCustomAgent_CannotUnregisterAnotherUsersAgent()
    {
        // Arrange - use a unique user for isolation
        var user1Connection = fixture.CreateHubConnection();
        await user1Connection.StartAsync();
        var user1Id = $"cross-unreg-user1-{Guid.NewGuid():N}";
        await user1Connection.InvokeAsync("RegisterUser", user1Id);

        var user2Connection = fixture.CreateHubConnection();
        await user2Connection.StartAsync();
        await user2Connection.InvokeAsync("RegisterUser", $"cross-unreg-user2-{Guid.NewGuid():N}");

        try
        {
            var registration = new CustomAgentRegistration
            {
                Name = "User1OnlyBot",
                Model = "gpt-4",
                McpServerEndpoints = []
            };
            var agentInfo = await user1Connection.InvokeAsync<AgentInfo>("RegisterCustomAgent", registration);

            // Act - user2 tries to unregister user1's agent
            var result = await user2Connection.InvokeAsync<bool>("UnregisterCustomAgent", agentInfo.Id);

            // Assert - should fail (return false) since user2 doesn't own it
            result.ShouldBeFalse();

            // Verify user1 still sees the agent
            var user1Agents = await user1Connection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");
            user1Agents.ShouldContain(a => a.Id == agentInfo.Id);
        }
        finally
        {
            await user1Connection.DisposeAsync();
            await user2Connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task RegisterCustomAgent_MultipleAgents_AllVisibleWithUniqueIds()
    {
        // Arrange - use a unique user for isolation
        var conn = fixture.CreateHubConnection();
        await conn.StartAsync();
        await conn.InvokeAsync("RegisterUser", $"multi-reg-user-{Guid.NewGuid():N}");

        try
        {
            var reg1 = new CustomAgentRegistration
            {
                Name = "Bot1",
                Model = "gpt-4",
                McpServerEndpoints = []
            };
            var reg2 = new CustomAgentRegistration
            {
                Name = "Bot2",
                Model = "claude-3",
                McpServerEndpoints = []
            };
            var agent1 = await conn.InvokeAsync<AgentInfo>("RegisterCustomAgent", reg1);
            var agent2 = await conn.InvokeAsync<AgentInfo>("RegisterCustomAgent", reg2);

            // Assert - both should have unique IDs
            agent1.Id.ShouldNotBe(agent2.Id);
            agent1.Id.ShouldStartWith("custom-");
            agent2.Id.ShouldStartWith("custom-");

            // GetAgents should include both custom agents plus built-in
            var agents = await conn.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");
            agents.ShouldContain(a => a.Id == agent1.Id);
            agents.ShouldContain(a => a.Id == agent2.Id);
            // Built-in (2) + custom (2)
            agents.Count.ShouldBe(4);
        }
        finally
        {
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetAgents_WithoutRegisteredUser_ReturnsBuiltInAgentsOnly()
    {
        // Arrange - create a fresh connection without calling RegisterUser
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        try
        {
            // Act - GetAgents should still work for unregistered users
            var agents = await unregisteredConnection.InvokeAsync<IReadOnlyList<AgentInfo>>("GetAgents");

            // Assert - should return built-in agents only (no throw, no custom agents)
            agents.ShouldNotBeNull();
            agents.ShouldContain(a => a.Id == "test-agent");
            agents.ShouldContain(a => a.Id == "second-agent");
            agents.ShouldAllBe(a => !a.Id.StartsWith("custom-"));
        }
        finally
        {
            await unregisteredConnection.DisposeAsync();
        }
    }
}
