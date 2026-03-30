using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class AgentDefinitionProviderTests
{
    private static readonly AgentDefinition _builtInAgent = new()
    {
        Id = "built-in",
        Name = "Built-In",
        Model = "test-model",
        McpServerEndpoints = [],
        EnabledFeatures = ["memory"]
    };

    private readonly CustomAgentRegistry _customAgentRegistry = new();
    private readonly AgentDefinitionProvider _sut;

    public AgentDefinitionProviderTests()
    {
        var registryOptions = new AgentRegistryOptions { Agents = [_builtInAgent] };
        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(registryOptions);

        _sut = new AgentDefinitionProvider(optionsMonitor.Object, _customAgentRegistry);
    }

    [Fact]
    public void GetById_BuiltInAgent_ReturnsDefinition()
    {
        var result = _sut.GetById("built-in");

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Built-In");
    }

    [Fact]
    public void GetById_CustomAgent_ReturnsDefinition()
    {
        var customDef = new AgentDefinition
        {
            Id = "custom-123",
            Name = "Custom",
            Model = "test-model",
            McpServerEndpoints = [],
            EnabledFeatures = []
        };
        _customAgentRegistry.Add("user1", customDef);

        var result = _sut.GetById("custom-123");

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Custom");
        result.EnabledFeatures.ShouldBeEmpty();
    }

    [Fact]
    public void GetById_UnknownAgent_ReturnsNull()
    {
        var result = _sut.GetById("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public void GetById_BuiltInTakesPrecedenceOverCustomWithSameId()
    {
        var customDef = new AgentDefinition
        {
            Id = "built-in",
            Name = "Impostor",
            Model = "test-model",
            McpServerEndpoints = []
        };
        _customAgentRegistry.Add("user1", customDef);

        var result = _sut.GetById("built-in");

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Built-In");
    }

    [Fact]
    public void GetById_RemovedCustomAgent_ReturnsNull()
    {
        var customDef = new AgentDefinition
        {
            Id = "custom-456",
            Name = "Temp",
            Model = "test-model",
            McpServerEndpoints = []
        };
        _customAgentRegistry.Add("user1", customDef);
        _customAgentRegistry.Remove("user1", "custom-456");

        var result = _sut.GetById("custom-456");

        result.ShouldBeNull();
    }

    [Fact]
    public void GetAll_ReturnsOnlyBuiltInAgents()
    {
        _customAgentRegistry.Add("user1", new AgentDefinition
        {
            Id = "custom-789",
            Name = "Custom",
            Model = "test-model",
            McpServerEndpoints = []
        });

        var result = _sut.GetAll();

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe("built-in");
    }
}
