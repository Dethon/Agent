using System.Text.RegularExpressions;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class MultiAgentFactoryTests
{
    private static readonly Regex _customIdPattern = new(
        "^custom-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.Compiled);

    private static readonly AgentDefinition _builtInAgent = new()
    {
        Id = "built-in-id",
        Name = "Built-In",
        Model = "test-model",
        McpServerEndpoints = []
    };

    private readonly MultiAgentFactory _sut;
    private readonly Mock<IToolApprovalHandler> _approvalHandler = new();

    public MultiAgentFactoryTests()
    {
        var registryOptions = new AgentRegistryOptions { Agents = [_builtInAgent] };

        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(registryOptions);

        var openRouterConfig = new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" };

        var domainToolRegistry = new Mock<IDomainToolRegistry>();
        domainToolRegistry
            .Setup(r => r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()))
            .Returns(Enumerable.Empty<AIFunction>());
        domainToolRegistry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<string>());

        var stateStore = new Mock<IThreadStateStore>();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IThreadStateStore)))
            .Returns(stateStore.Object);

        _sut = new MultiAgentFactory(
            serviceProvider.Object,
            optionsMonitor.Object,
            openRouterConfig,
            domainToolRegistry.Object);
    }

    private static CustomAgentRegistration MakeRegistration(
        string name = "TestBot",
        string model = "test-model",
        string? description = null) => new()
        {
            Name = name,
            Description = description,
            Model = model,
            McpServerEndpoints = []
        };

    // --- RegisterCustomAgent ---

    [Fact]
    public void RegisterCustomAgent_ReturnsAgentInfoWithCustomPrefixedId()
    {
        var registration = MakeRegistration(name: "MyBot", model: "gpt-4", description: "A custom bot");

        var result = _sut.RegisterCustomAgent("user1", registration);

        result.ShouldNotBeNull();
        _customIdPattern.IsMatch(result.Id).ShouldBeTrue($"Id '{result.Id}' should match custom-{{guid}} pattern");
        result.Name.ShouldBe("MyBot");
        result.Description.ShouldBe("A custom bot");
    }

    [Fact]
    public void RegisterCustomAgent_TwoAgentsSameUser_BothStoredWithDifferentIds()
    {
        _sut.RegisterCustomAgent("user1", MakeRegistration(name: "Bot1", model: "m1"));
        _sut.RegisterCustomAgent("user1", MakeRegistration(name: "Bot2", model: "m2"));

        var agents = _sut.GetAvailableAgents("user1");

        agents.Count.ShouldBe(3); // 1 built-in + 2 custom
        agents.Select(a => a.Name).ShouldContain("Bot1");
        agents.Select(a => a.Name).ShouldContain("Bot2");
        agents.Select(a => a.Id).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void RegisterCustomAgent_DifferentUsers_AgentsIsolated()
    {
        _sut.RegisterCustomAgent("user1", MakeRegistration(name: "UserOneBot"));
        _sut.RegisterCustomAgent("user2", MakeRegistration(name: "UserTwoBot"));

        var user1Agents = _sut.GetAvailableAgents("user1");
        var user2Agents = _sut.GetAvailableAgents("user2");

        user1Agents.Select(a => a.Name).ShouldContain("UserOneBot");
        user1Agents.Select(a => a.Name).ShouldNotContain("UserTwoBot");
        user2Agents.Select(a => a.Name).ShouldContain("UserTwoBot");
        user2Agents.Select(a => a.Name).ShouldNotContain("UserOneBot");
    }

    // --- UnregisterCustomAgent ---

    [Fact]
    public void UnregisterCustomAgent_ExistingAgent_ReturnsTrue()
    {
        var info = _sut.RegisterCustomAgent("user1", MakeRegistration());

        var result = _sut.UnregisterCustomAgent("user1", info.Id);

        result.ShouldBeTrue();
    }

    [Fact]
    public void UnregisterCustomAgent_NonExistentAgent_ReturnsFalse()
    {
        var result = _sut.UnregisterCustomAgent("user1", "custom-nonexistent");

        result.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterCustomAgent_WrongUser_ReturnsFalse()
    {
        var info = _sut.RegisterCustomAgent("user1", MakeRegistration());

        var result = _sut.UnregisterCustomAgent("user2", info.Id);

        result.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterCustomAgent_AgentRemovedFromList()
    {
        var info = _sut.RegisterCustomAgent("user1", MakeRegistration());
        _sut.UnregisterCustomAgent("user1", info.Id);

        var agents = _sut.GetAvailableAgents("user1");

        agents.Count.ShouldBe(1); // only built-in
        agents.Select(a => a.Id).ShouldNotContain(info.Id);
    }

    // --- GetAvailableAgents ---

    [Fact]
    public void GetAvailableAgents_NullUserId_ReturnsOnlyBuiltInAgents()
    {
        _sut.RegisterCustomAgent("user1", MakeRegistration(name: "Custom1"));

        var agents = _sut.GetAvailableAgents();

        agents.Count.ShouldBe(1);
        agents.ShouldAllBe(a => !a.Id.StartsWith("custom-"));
    }

    [Fact]
    public void GetAvailableAgents_WithUserId_NoCustomAgents_ReturnsBuiltIn()
    {
        var agents = _sut.GetAvailableAgents("user1");

        var nullAgents = _sut.GetAvailableAgents();
        agents.Count.ShouldBe(nullAgents.Count);
    }

    [Fact]
    public void GetAvailableAgents_WithUserId_WithCustomAgents_MergesAgents()
    {
        _sut.RegisterCustomAgent("user1", MakeRegistration(name: "Custom1"));

        var agents = _sut.GetAvailableAgents("user1");

        agents.Count.ShouldBe(2); // 1 built-in + 1 custom
        agents.Select(a => a.Name).ShouldContain("Custom1");
        agents.Select(a => a.Name).ShouldContain("Built-In");
    }

    // --- Create ---

    [Fact]
    public void Create_WithCustomAgentId_CreatesAgent()
    {
        var info = _sut.RegisterCustomAgent("user1", MakeRegistration());
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, "user1", info.Id, _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }

    [Fact]
    public void Create_WithUnknownAgentId_Throws()
    {
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = Should.Throw<InvalidOperationException>(
            () => _sut.Create(agentKey, "user1", "unknown-id", _approvalHandler.Object));

        ex.Message.ShouldContain("unknown-id");
    }

    [Fact]
    public void Create_WithBuiltInAgentId_StillWorks()
    {
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, "user1", "built-in-id", _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }

    // --- Adversarial edge-case tests ---

    [Fact]
    public void Create_AfterUnregister_Throws()
    {
        var info = _sut.RegisterCustomAgent("user1", MakeRegistration());
        _sut.UnregisterCustomAgent("user1", info.Id);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = Should.Throw<InvalidOperationException>(
            () => _sut.Create(agentKey, "user1", info.Id, _approvalHandler.Object));

        ex.Message.ShouldContain(info.Id);
    }

    [Fact]
    public void Create_WithCustomAgentIdOfDifferentUser_Throws()
    {
        var info = _sut.RegisterCustomAgent("user1", MakeRegistration());
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = Should.Throw<InvalidOperationException>(
            () => _sut.Create(agentKey, "user2", info.Id, _approvalHandler.Object));

        ex.Message.ShouldContain(info.Id);
    }

    [Fact]
    public void RegisterCustomAgent_NullDescription_ReturnsNullDescription()
    {
        var result = _sut.RegisterCustomAgent("user1", MakeRegistration(description: null));

        result.Description.ShouldBeNull();
    }

    [Fact]
    public void RegisterCustomAgent_AllFieldsMapped_CreateSucceeds()
    {
        var registration = new CustomAgentRegistration
        {
            Name = "FullBot",
            Description = "Full description",
            Model = "test-model",
            McpServerEndpoints = [],
            WhitelistPatterns = ["pattern1"],
            CustomInstructions = "Be helpful",
            EnabledFeatures = ["feature1"]
        };
        var info = _sut.RegisterCustomAgent("user1", registration);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, "user1", info.Id, _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }

    [Fact]
    public void GetAvailableAgents_BuiltInAgentsAlwaysFirst()
    {
        _sut.RegisterCustomAgent("user1", MakeRegistration(name: "Custom1"));

        var agents = _sut.GetAvailableAgents("user1");

        agents.First().Id.ShouldBe("built-in-id");
    }
}