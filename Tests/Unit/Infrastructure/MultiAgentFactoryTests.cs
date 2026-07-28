using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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

    private static readonly AgentDefinition _fullyMappedAgent = new()
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

    public static IEnumerable<object[]> SuccessCases =>
    [
        ["null agent id", "user1", (Func<MultiAgentFactoryTests, string?>)(_ => null)],
        ["built-in agent id", "user1", (Func<MultiAgentFactoryTests, string?>)(_ => _builtInAgent.Id)],
        ["custom agent id for owning user", "user1", (Func<MultiAgentFactoryTests, string?>)(t => t.AddCustomAgent("user1").Id)],
        ["custom agent with all fields populated", "user1", (Func<MultiAgentFactoryTests, string?>)(t =>
        {
            t._customAgentRegistry.Add("user1", _fullyMappedAgent);
            return _fullyMappedAgent.Id;
        })]
    ];

    [Theory]
    [MemberData(nameof(SuccessCases))]
    public void Create_SupportedAgentIdentifier_ReturnsAgent(string _, string userId, Func<MultiAgentFactoryTests, string?> agentIdFactory)
    {
        var agentId = agentIdFactory(this);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, userId, agentId, _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }

    public static IEnumerable<object?[]> ErrorCases =>
    [
        ["unknown agent id", "user1", (Func<MultiAgentFactoryTests, string>)(_ => "unknown-id"), "unknown-id"],
        ["custom agent unregistered before create", "user1", (Func<MultiAgentFactoryTests, string>)(t =>
        {
            var def = t.AddCustomAgent("user1");
            t._customAgentRegistry.Remove("user1", def.Id);
            return def.Id;
        }), null],
        ["custom agent owned by a different user", "user2", (Func<MultiAgentFactoryTests, string>)(t => t.AddCustomAgent("user1").Id), null]
    ];

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public void Create_RejectsInvalidAgentId_Throws(string _, string userId, Func<MultiAgentFactoryTests, string> agentIdFactory, string? expectedMessageFragment)
    {
        var agentId = agentIdFactory(this);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = Should.Throw<InvalidOperationException>(
            () => _sut.Create(agentKey, userId, agentId, _approvalHandler.Object));

        ex.Message.ShouldContain(expectedMessageFragment ?? agentId);
    }

    [Fact]
    public void Create_AgentDeclaresRouting_UsesItsOwnAndNotTheGlobalDefault()
    {
        var agentRouting = new ProviderRouting { Sort = ProviderSort.Latency };
        var (factory, captured, _) = CreateCapturingFactory(
            new ProviderRouting { Sort = ProviderSort.Throughput },
            RoutedAgent("routed", agentRouting));

        factory.Create(new AgentKey("1:1", "test"), "user1", "routed", _approvalHandler.Object);

        captured.Single().ShouldBe(agentRouting);
    }

    [Fact]
    public void Create_AgentDeclaresNoRouting_InheritsTheGlobalDefault()
    {
        var globalRouting = new ProviderRouting { Sort = ProviderSort.Throughput };
        var (factory, captured, _) = CreateCapturingFactory(globalRouting, RoutedAgent("plain", null));

        factory.Create(new AgentKey("1:1", "test"), "user1", "plain", _approvalHandler.Object);

        captured.Single().ShouldBe(globalRouting);
    }

    // Balanced routing is the absence of a provider object, so "neither set" must resolve to
    // null rather than to some empty-but-present default.
    [Fact]
    public void Create_NeitherAgentNorGlobalDeclaresRouting_ResolvesToNull()
    {
        var (factory, captured, _) = CreateCapturingFactory(null, RoutedAgent("plain", null));

        factory.Create(new AgentKey("1:1", "test"), "user1", "plain", _approvalHandler.Object);

        captured.Single().ShouldBeNull();
    }

    [Fact]
    public void CreateSubAgent_DeclaresRouting_UsesItsOwnAndNotTheGlobalDefault()
    {
        var subRouting = new ProviderRouting { Sort = ProviderSort.Throughput };
        var (factory, captured, _) = CreateCapturingFactory(
            new ProviderRouting { Sort = ProviderSort.Price });

        factory.CreateSubAgent(
            RoutedSubAgent(subRouting), _approvalHandler.Object, [], "user1");

        captured.Single().ShouldBe(subRouting);
    }

    [Fact]
    public void CreateSubAgent_DeclaresNoRouting_InheritsTheGlobalDefault()
    {
        var globalRouting = new ProviderRouting { Sort = ProviderSort.Price };
        var (factory, captured, _) = CreateCapturingFactory(globalRouting);

        factory.CreateSubAgent(RoutedSubAgent(null), _approvalHandler.Object, [], "user1");

        captured.Single().ShouldBe(globalRouting);
    }

    [Fact]
    public void Create_RoutingTripsAnAdvisory_LogsAWarningNamingTheAgent()
    {
        var routing = new ProviderRouting { Order = ["deepinfra"] };
        var (factory, _, logs) = CreateCapturingFactory(null, RoutedAgent("noisy", routing));

        factory.Create(new AgentKey("1:1", "test"), "user1", "noisy", _approvalHandler.Object);

        logs.ShouldContain(m => m.Contains("noisy") && m.Contains("sticky routing"));
    }

    // Asserts the absence of an advisory rather than an empty log: agent construction may warn
    // about unrelated things, and this test must not become a tripwire for those.
    [Fact]
    public void Create_RoutingIsClean_LogsNoAdvisory()
    {
        var routing = new ProviderRouting { Sort = ProviderSort.Latency };
        var (factory, _, logs) = CreateCapturingFactory(null, RoutedAgent("quiet", routing));

        factory.Create(new AgentKey("1:1", "test"), "user1", "quiet", _approvalHandler.Object);

        logs.ShouldNotContain(m => m.Contains("sticky routing") || m.Contains("providerRouting.sort"));
    }

    private static AgentDefinition RoutedAgent(string id, ProviderRouting? routing) => new()
    {
        Id = id,
        Name = id,
        Model = "z-ai/glm-5.2",
        McpServerEndpoints = [],
        ProviderRouting = routing
    };

    private static SubAgentDefinition RoutedSubAgent(ProviderRouting? routing) => new()
    {
        Id = "worker",
        Name = "Worker",
        Model = "z-ai/glm-5.2",
        McpServerEndpoints = [],
        ProviderRouting = routing
    };

    private (MultiAgentFactory Factory, List<ProviderRouting?> Captured, List<string> Logs)
        CreateCapturingFactory(ProviderRouting? globalRouting, params AgentDefinition[] agents)
    {
        var captured = new List<ProviderRouting?>();
        var logProvider = new CapturingLoggerProvider();

        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(new AgentRegistryOptions { Agents = agents });

        var domainToolRegistry = new Mock<IDomainToolRegistry>();
        domainToolRegistry
            .Setup(r => r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()))
            .Returns(Enumerable.Empty<AIFunction>());
        domainToolRegistry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<string>());

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IThreadStateStore)))
            .Returns(new Mock<IThreadStateStore>().Object);

        var factory = new MultiAgentFactory(
            serviceProvider.Object,
            new AgentDefinitionProvider(optionsMonitor.Object, new CustomAgentRegistry()),
            new OpenRouterConfig
            {
                ApiUrl = "http://test",
                ApiKey = "test-key",
                ProviderRouting = globalRouting
            },
            domainToolRegistry.Object,
            loggerFactory: LoggerFactory.Create(b => b.AddProvider(logProvider)),
            chatClientFactory: (_, _, _, routing) =>
            {
                captured.Add(routing);
                return new Mock<IChatClient>().Object;
            });

        return (factory, captured, logProvider.Messages);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}