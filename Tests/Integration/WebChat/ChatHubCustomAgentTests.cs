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
        ex.Message.ShouldContain("Name", Case.Insensitive);
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
        ex.Message.ShouldContain("Name", Case.Insensitive);
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
        ex.Message.ShouldContain("Model", Case.Insensitive);
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
        ex.Message.ShouldContain("Model", Case.Insensitive);
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
            ex.Message.ShouldContain("not registered", Case.Insensitive);
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
            ex.Message.ShouldContain("not registered", Case.Insensitive);
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
}
