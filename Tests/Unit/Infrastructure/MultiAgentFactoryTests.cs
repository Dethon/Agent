using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class MultiAgentFactoryTests
{
    private static readonly AgentDefinition _builtInAgent = new()
    {
        Id = "built-in-id",
        Name = "Built-In",
        Model = "test-model",
        McpServerEndpoints = []
    };

    private readonly CustomAgentRegistry _customAgentRegistry = new();
    private readonly AgentDefinitionProvider _definitionProvider;
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

        _definitionProvider = new AgentDefinitionProvider(optionsMonitor.Object, _customAgentRegistry);

        _sut = new MultiAgentFactory(
            serviceProvider.Object,
            _definitionProvider,
            openRouterConfig,
            domainToolRegistry.Object,
            null);
    }

    private AgentDefinition AddCustomAgent(string userId, string name = "TestBot", string model = "test-model")
    {
        var definition = new AgentDefinition
        {
            Id = $"custom-{Guid.NewGuid()}",
            Name = name,
            Model = model,
            McpServerEndpoints = []
        };
        _customAgentRegistry.Add(userId, definition);
        return definition;
    }

    // --- Create ---

    [Fact]
    public void Create_WithNullAgentId_ReturnsDefaultAgent()
    {
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, "user1", null, _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }

    [Fact]
    public void Create_WithBuiltInAgentId_CreatesAgent()
    {
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, "user1", "built-in-id", _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }

    [Fact]
    public void Create_WithCustomAgentId_CreatesAgent()
    {
        var custom = AddCustomAgent("user1");
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, "user1", custom.Id, _approvalHandler.Object);

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
    public void Create_AfterUnregister_Throws()
    {
        var custom = AddCustomAgent("user1");
        _customAgentRegistry.Remove("user1", custom.Id);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = Should.Throw<InvalidOperationException>(
            () => _sut.Create(agentKey, "user1", custom.Id, _approvalHandler.Object));

        ex.Message.ShouldContain(custom.Id);
    }

    [Fact]
    public void Create_WithCustomAgentIdOfDifferentUser_Throws()
    {
        var custom = AddCustomAgent("user1");
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = Should.Throw<InvalidOperationException>(
            () => _sut.Create(agentKey, "user2", custom.Id, _approvalHandler.Object));

        ex.Message.ShouldContain(custom.Id);
    }

    [Fact]
    public void Create_WithAllFieldsMapped_Succeeds()
    {
        var definition = new AgentDefinition
        {
            Id = "custom-full",
            Name = "FullBot",
            Description = "Full description",
            Model = "test-model",
            McpServerEndpoints = [],
            WhitelistPatterns = ["pattern1"],
            CustomInstructions = "Be helpful",
            EnabledFeatures = ["feature1"]
        };
        _customAgentRegistry.Add("user1", definition);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, "user1", "custom-full", _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }
}