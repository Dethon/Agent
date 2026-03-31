# Move Custom Agent Registration to AgentDefinitionProvider — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move custom agent registration/unregistration/query logic from `MultiAgentFactory` into `AgentDefinitionProvider`, so the factory is purely responsible for creating agent instances and the provider owns all definition management.

**Architecture:** `IAgentDefinitionProvider` expands with `RegisterCustomAgent`, `UnregisterCustomAgent`, and a userId-aware `GetAll` overload. `IAgentFactory` shrinks to only creation methods. `MultiAgentFactory` loses its `CustomAgentRegistry` and `IOptionsMonitor<AgentRegistryOptions>` dependencies, taking `IAgentDefinitionProvider` instead. API endpoints in `Program.cs` switch to injecting the provider for definition-management routes.

**Tech Stack:** .NET 10, C# 14, xUnit, Shouldly, Moq

---

### Task 1: Expand `IAgentDefinitionProvider` with Registration Methods

**Files:**
- Modify: `Domain/Contracts/IAgentDefinitionProvider.cs`

- [ ] **Step 1: Update the interface**

Add three new methods and update `GetAll` to accept an optional `userId`:

```csharp
using Domain.DTOs;
using Domain.DTOs.WebChat;

namespace Domain.Contracts;

public interface IAgentDefinitionProvider
{
    AgentDefinition? GetById(string agentId);
    IReadOnlyList<AgentDefinition> GetAll(string? userId = null);
    AgentDefinition RegisterCustomAgent(string userId, CustomAgentRegistration registration);
    bool UnregisterCustomAgent(string userId, string agentId);
}
```

- [ ] **Step 2: Verify solution compiles with errors**

Run: `dotnet build Agent/Agent.csproj 2>&1 | tail -5`
Expected: Build failure — `AgentDefinitionProvider` does not implement the new members. This confirms the interface change is picked up.

- [ ] **Step 3: Commit**

```bash
git add Domain/Contracts/IAgentDefinitionProvider.cs
git commit -m "refactor: expand IAgentDefinitionProvider with registration methods"
```

---

### Task 2: Write Failing Tests for New Provider Methods

**Files:**
- Modify: `Tests/Unit/Infrastructure/AgentDefinitionProviderTests.cs`

- [ ] **Step 1: Add tests for `RegisterCustomAgent`, `UnregisterCustomAgent`, and `GetAll(userId)`**

Append these tests to the existing `AgentDefinitionProviderTests` class:

```csharp
// --- RegisterCustomAgent ---

[Fact]
public void RegisterCustomAgent_ReturnsDefinitionWithCustomPrefixedId()
{
    var registration = new CustomAgentRegistration
    {
        Name = "MyBot",
        Description = "A custom bot",
        Model = "gpt-4",
        McpServerEndpoints = []
    };

    var result = _sut.RegisterCustomAgent("user1", registration);

    result.ShouldNotBeNull();
    result.Id.ShouldStartWith("custom-");
    result.Name.ShouldBe("MyBot");
    result.Description.ShouldBe("A custom bot");
    result.Model.ShouldBe("gpt-4");
}

[Fact]
public void RegisterCustomAgent_TwoAgentsSameUser_BothStoredWithDifferentIds()
{
    var reg1 = new CustomAgentRegistration { Name = "Bot1", Model = "m1", McpServerEndpoints = [] };
    var reg2 = new CustomAgentRegistration { Name = "Bot2", Model = "m2", McpServerEndpoints = [] };

    var def1 = _sut.RegisterCustomAgent("user1", reg1);
    var def2 = _sut.RegisterCustomAgent("user1", reg2);

    def1.Id.ShouldNotBe(def2.Id);
    var all = _sut.GetAll("user1");
    all.Count.ShouldBe(3); // 1 built-in + 2 custom
    all.Select(a => a.Name).ShouldContain("Bot1");
    all.Select(a => a.Name).ShouldContain("Bot2");
}

[Fact]
public void RegisterCustomAgent_DifferentUsers_AgentsIsolated()
{
    _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "UserOneBot", Model = "m1", McpServerEndpoints = [] });
    _sut.RegisterCustomAgent("user2", new CustomAgentRegistration { Name = "UserTwoBot", Model = "m1", McpServerEndpoints = [] });

    var user1All = _sut.GetAll("user1");
    var user2All = _sut.GetAll("user2");

    user1All.Select(a => a.Name).ShouldContain("UserOneBot");
    user1All.Select(a => a.Name).ShouldNotContain("UserTwoBot");
    user2All.Select(a => a.Name).ShouldContain("UserTwoBot");
    user2All.Select(a => a.Name).ShouldNotContain("UserOneBot");
}

[Fact]
public void RegisterCustomAgent_AllFieldsMapped()
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

    var result = _sut.RegisterCustomAgent("user1", registration);

    result.WhitelistPatterns.ShouldBe(["pattern1"]);
    result.CustomInstructions.ShouldBe("Be helpful");
    result.EnabledFeatures.ShouldBe(["feature1"]);
}

[Fact]
public void RegisterCustomAgent_NullDescription_ReturnsNullDescription()
{
    var result = _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

    result.Description.ShouldBeNull();
}

// --- UnregisterCustomAgent ---

[Fact]
public void UnregisterCustomAgent_ExistingAgent_ReturnsTrue()
{
    var def = _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

    var result = _sut.UnregisterCustomAgent("user1", def.Id);

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
    var def = _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });

    var result = _sut.UnregisterCustomAgent("user2", def.Id);

    result.ShouldBeFalse();
}

[Fact]
public void UnregisterCustomAgent_AgentRemovedFromList()
{
    var def = _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Bot", Model = "m1", McpServerEndpoints = [] });
    _sut.UnregisterCustomAgent("user1", def.Id);

    var all = _sut.GetAll("user1");

    all.Count.ShouldBe(1); // only built-in
    all.Select(a => a.Id).ShouldNotContain(def.Id);
}

// --- GetAll (updated) ---

[Fact]
public void GetAll_NullUserId_ReturnsOnlyBuiltInAgents()
{
    _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Custom1", Model = "m1", McpServerEndpoints = [] });

    var result = _sut.GetAll();

    result.Count.ShouldBe(1);
    result.ShouldAllBe(a => !a.Id.StartsWith("custom-"));
}

[Fact]
public void GetAll_WithUserId_MergesBuiltInAndCustom()
{
    _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Custom1", Model = "m1", McpServerEndpoints = [] });

    var result = _sut.GetAll("user1");

    result.Count.ShouldBe(2); // 1 built-in + 1 custom
    result.Select(a => a.Name).ShouldContain("Custom1");
    result.Select(a => a.Name).ShouldContain("Built-In");
}

[Fact]
public void GetAll_BuiltInAgentsAlwaysFirst()
{
    _sut.RegisterCustomAgent("user1", new CustomAgentRegistration { Name = "Custom1", Model = "m1", McpServerEndpoints = [] });

    var result = _sut.GetAll("user1");

    result.First().Id.ShouldBe("built-in");
}
```

Also add the using for `CustomAgentRegistration` at the top of the file:

```csharp
using Domain.DTOs.WebChat;
```

Also update the existing `GetAll_ReturnsOnlyBuiltInAgents` test since `GetAll()` now takes an optional parameter — it should still pass as-is since default is `null`, but rename it for clarity:

Replace:
```csharp
[Fact]
public void GetAll_ReturnsOnlyBuiltInAgents()
```
With:
```csharp
[Fact]
public void GetAll_NoUserId_ReturnsOnlyBuiltInAgents()
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentDefinitionProviderTests" --no-restore 2>&1 | tail -20`
Expected: Build failure — `AgentDefinitionProvider` does not implement `RegisterCustomAgent` / `UnregisterCustomAgent` / `GetAll(string?)`.

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Infrastructure/AgentDefinitionProviderTests.cs
git commit -m "test(red): add failing tests for provider registration methods"
```

---

### Task 3: Implement Registration Methods in `AgentDefinitionProvider`

**Files:**
- Modify: `Infrastructure/Agents/AgentDefinitionProvider.cs`

- [ ] **Step 1: Implement the new methods and update `GetAll`**

Replace the full file content:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Options;

namespace Infrastructure.Agents;

public class AgentDefinitionProvider(
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    CustomAgentRegistry customAgentRegistry) : IAgentDefinitionProvider
{
    public AgentDefinition? GetById(string agentId)
    {
        return registryOptions.CurrentValue.Agents
            .FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))
            ?? customAgentRegistry.FindById(agentId);
    }

    public IReadOnlyList<AgentDefinition> GetAll(string? userId = null)
    {
        var builtIn = registryOptions.CurrentValue.Agents.ToList();

        if (userId is not null)
        {
            builtIn.AddRange(customAgentRegistry.GetByUser(userId));
        }

        return builtIn;
    }

    public AgentDefinition RegisterCustomAgent(string userId, CustomAgentRegistration registration)
    {
        var definition = new AgentDefinition
        {
            Id = $"custom-{Guid.NewGuid()}",
            Name = registration.Name,
            Description = registration.Description,
            Model = registration.Model,
            McpServerEndpoints = registration.McpServerEndpoints,
            WhitelistPatterns = registration.WhitelistPatterns,
            CustomInstructions = registration.CustomInstructions,
            EnabledFeatures = registration.EnabledFeatures
        };

        customAgentRegistry.Add(userId, definition);

        return definition;
    }

    public bool UnregisterCustomAgent(string userId, string agentId)
    {
        return customAgentRegistry.Remove(userId, agentId);
    }
}
```

- [ ] **Step 2: Run the new provider tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentDefinitionProviderTests" --no-restore 2>&1 | tail -20`
Expected: All `AgentDefinitionProviderTests` pass (including both old and new tests).

- [ ] **Step 3: Commit**

```bash
git add Infrastructure/Agents/AgentDefinitionProvider.cs
git commit -m "feat(green): implement registration methods in AgentDefinitionProvider"
```

---

### Task 4: Shrink `IAgentFactory` and Update `MultiAgentFactory`

**Files:**
- Modify: `Domain/Contracts/IAgentFactory.cs`
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`

- [ ] **Step 1: Remove definition-management methods from `IAgentFactory`**

Replace the full file:

```csharp
using Domain.Agents;
using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgentFactory
{
    DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler);
    DisposableAgent CreateSubAgent(SubAgentDefinition definition, IToolApprovalHandler approvalHandler, string[] whitelistPatterns, string userId);
}
```

- [ ] **Step 2: Refactor `MultiAgentFactory` to use `IAgentDefinitionProvider`**

Replace the full file:

```csharp
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Agents;

public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IAgentDefinitionProvider definitionProvider,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null) : IAgentFactory, IScheduleAgentFactory
{

    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        var agents = definitionProvider.GetAll(userId);

        var definition = string.IsNullOrEmpty(agentId)
            ? agents.FirstOrDefault()
            : agents.FirstOrDefault(a => a.Id == agentId);

        _ = definition ?? throw new InvalidOperationException(
            string.IsNullOrEmpty(agentId)
                ? "No agents configured."
                : $"No agent found for identifier '{agentId}'.");

        return CreateFromDefinition(agentKey, userId, definition, approvalHandler);
    }

    public DisposableAgent CreateSubAgent(
        SubAgentDefinition definition,
        IToolApprovalHandler approvalHandler,
        string[] whitelistPatterns,
        string userId)
    {
        var agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, definition.Name)
            : null;

        var chatClient = CreateChatClient(definition.Model, agentPublisher);

        var effectiveClient = new ToolApprovalChatClient(
            chatClient,
            approvalHandler,
            whitelistPatterns,
            agentPublisher);

        var enabledFeatures = definition.EnabledFeatures
            .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase));

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => CreateSubAgent(def, approvalHandler, whitelistPatterns, userId));
        var domainTools = domainToolRegistry
            .GetToolsForFeatures(enabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(enabledFeatures)
            .ToList();

        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            $"subagent-{definition.Id}",
            definition.Description ?? "",
            new NullThreadStateStore(),
            userId,
            definition.CustomInstructions,
            domainTools,
            domainPrompts,
            enableResourceSubscriptions: false);
    }

    public DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
    {
        var agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, definition.Name)
            : metricsPublisher;
        var chatClient = CreateChatClient(definition.Model, agentPublisher);
        var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

        var name = $"{definition.Name}-{agentKey.ConversationId}";
        var effectiveClient = new ToolApprovalChatClient(chatClient, approvalHandler, definition.WhitelistPatterns, agentPublisher);

        var featureConfig = new FeatureConfig(
            SubAgentFactory: def => CreateSubAgent(def, approvalHandler, definition.WhitelistPatterns, userId));
        var domainTools = domainToolRegistry
            .GetToolsForFeatures(definition.EnabledFeatures, featureConfig)
            .ToList();
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(definition.EnabledFeatures)
            .ToList();

        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            name,
            definition.Description ?? "",
            stateStore,
            userId,
            definition.CustomInstructions,
            domainTools,
            domainPrompts);
    }

    private OpenRouterChatClient CreateChatClient(string model, IMetricsPublisher? publisher = null)
    {
        return new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            model,
            publisher ?? metricsPublisher);
    }
}

public record OpenRouterConfig
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
}

public sealed class AgentRegistryOptions
{
    public AgentDefinition[] Agents { get; set; } = [];
}
```

Note on `Create()`: We use `GetAll(userId)` which merges built-in + user's custom agents, then find by ID. This preserves user isolation — a user can only access built-in agents and their own custom agents, matching the original behavior.

- [ ] **Step 3: Verify build compiles (expect failures from tests and other callers)**

Run: `dotnet build Agent/Agent.csproj 2>&1 | tail -10`
Expected: Agent project builds. Test project and other callers may fail (fixed in later tasks).

- [ ] **Step 4: Commit**

```bash
git add Domain/Contracts/IAgentFactory.cs Infrastructure/Agents/MultiAgentFactory.cs
git commit -m "refactor: remove definition-management from IAgentFactory, use provider in MultiAgentFactory"
```

---

### Task 5: Update API Endpoints in `Program.cs`

**Files:**
- Modify: `Agent/Program.cs`

- [ ] **Step 1: Switch endpoints to use `IAgentDefinitionProvider`**

Replace lines 24-31:

```csharp
app.MapGet("/api/agents", (IAgentDefinitionProvider provider, string? userId) =>
    provider.GetAll(userId).Select(a => new AgentInfo(a.Id, a.Name, a.Description)));

app.MapPost("/api/agents", (IAgentDefinitionProvider provider, string userId, CustomAgentRegistration registration) =>
{
    var definition = provider.RegisterCustomAgent(userId, registration);
    return new AgentInfo(definition.Id, definition.Name, definition.Description);
});

app.MapDelete("/api/agents/{agentId}", (IAgentDefinitionProvider provider, string userId, string agentId) =>
    provider.UnregisterCustomAgent(userId, agentId));
```

Also update the usings at the top — add `Domain.DTOs` (for `AgentDefinition`) if not already present. The file should have:

```csharp
using Agent.Modules;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
```

- [ ] **Step 2: Commit**

```bash
git add Agent/Program.cs
git commit -m "refactor: switch API endpoints to IAgentDefinitionProvider"
```

---

### Task 6: Update DI Registration in `InjectorModule`

**Files:**
- Modify: `Agent/Modules/InjectorModule.cs`

- [ ] **Step 1: Update `MultiAgentFactory` registration to use `IAgentDefinitionProvider`**

The factory no longer takes `IOptionsMonitor<AgentRegistryOptions>` or `CustomAgentRegistry`. It takes `IAgentDefinitionProvider` instead. Also register `IAgentDefinitionProvider` in this module (move from `SchedulingModule` since it's now a core dependency, not scheduling-specific).

Replace the `AddAgent` method body:

```csharp
public IServiceCollection AddAgent(AgentSettings settings)
{
    var llmConfig = new OpenRouterConfig
    {
        ApiUrl = settings.OpenRouter.ApiUrl,
        ApiKey = settings.OpenRouter.ApiKey
    };

    services.Configure<AgentRegistryOptions>(options => options.Agents = settings.Agents);

    return services
        .AddRedis(settings.Redis)
        .AddSingleton<IMetricsPublisher, RedisMetricsPublisher>()
        .AddHostedService(sp =>
            new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "agent"))
        .AddSingleton<ChatThreadResolver>()
        .AddSingleton<IDomainToolRegistry, DomainToolRegistry>()
        .AddSingleton<CustomAgentRegistry>()
        .AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>()
        .AddSingleton<IAgentFactory>(sp =>
            new MultiAgentFactory(
                sp,
                sp.GetRequiredService<IAgentDefinitionProvider>(),
                llmConfig,
                sp.GetRequiredService<IDomainToolRegistry>(),
                sp.GetRequiredService<IMetricsPublisher>()))
        .AddSingleton<IScheduleAgentFactory>(sp =>
            (IScheduleAgentFactory)sp.GetRequiredService<IAgentFactory>());
}
```

- [ ] **Step 2: Remove `IAgentDefinitionProvider` registration from `SchedulingModule`**

In `Agent/Modules/SchedulingModule.cs`, remove this line:

```csharp
services.AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>();
```

Also remove the unused `using Infrastructure.Agents;` import if no other references remain in the file. Check first — `AgentDefinitionProvider` was the only reason for that import.

- [ ] **Step 3: Verify the Agent project builds**

Run: `dotnet build Agent/Agent.csproj 2>&1 | tail -10`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Agent/Modules/InjectorModule.cs Agent/Modules/SchedulingModule.cs
git commit -m "refactor: move IAgentDefinitionProvider registration to InjectorModule, update factory DI"
```

---

### Task 7: Update Test Fakes and Stubs

**Files:**
- Modify: `Tests/Integration/Fixtures/FakeAgentFactory.cs`
- Modify: `Tests/Unit/Domain/MonitorTests.cs:80-99`

- [ ] **Step 1: Remove definition-management methods from `FakeAgentFactory` (integration fixture)**

In `Tests/Integration/Fixtures/FakeAgentFactory.cs`, remove lines 89-96 (the three delegated methods) and the `_inner` / `MultiAgentFactory` dependency since it was only used for those methods.

Replace the class to remove all registration-related code:

Remove these fields:
```csharp
private readonly MultiAgentFactory _inner;
```

Remove from the constructor:
```csharp
var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
optionsMonitor.Setup(o => o.CurrentValue).Returns(() => _registryOptions);

_inner = new MultiAgentFactory(
    new Mock<IServiceProvider>().Object,
    optionsMonitor.Object,
    new OpenRouterConfig { ApiUrl = "http://fake", ApiKey = "fake" },
    new Mock<IDomainToolRegistry>().Object,
    new CustomAgentRegistry());
```

Remove these methods entirely:
```csharp
public IReadOnlyList<AgentInfo> GetAvailableAgents(string? userId = null)
    => _inner.GetAvailableAgents(userId);

public AgentInfo RegisterCustomAgent(string userId, CustomAgentRegistration registration)
    => _inner.RegisterCustomAgent(userId, registration);

public bool UnregisterCustomAgent(string userId, string agentId)
    => _inner.UnregisterCustomAgent(userId, agentId);
```

Keep `ConfigureAgents` and `_registryOptions` — they're used for test setup but they work independently (just stores `AgentDefinition[]`). But actually, check if `ConfigureAgents` is used anywhere... it configures the `_registryOptions` which was passed to `_inner`. Since `_inner` is gone, `ConfigureAgents` and `_registryOptions` are also unused. Check callers:

Run: `grep -rn "ConfigureAgents\|_registryOptions" Tests/` to verify usage.

If `ConfigureAgents` is called from integration tests, keep the field and method but remove the `_inner` dependency. If unused, remove them too.

Remove unused usings: `Infrastructure.Agents`, `Microsoft.Extensions.Options`, `Moq`, `Domain.DTOs.WebChat` (check each).

The final `FakeAgentFactory` should look like:

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Tests.Integration.Fixtures;

public sealed class FakeAgentFactory : IAgentFactory
{
    private readonly ConcurrentQueue<QueuedResponse> _responseQueue = new();
    private readonly AgentRegistryOptions _registryOptions = new();
    private const int ResponseDelayMs = 10;

    public void ConfigureAgents(params AgentDefinition[] agents)
    {
        _registryOptions.Agents = agents;
    }

    // ... rest of enqueue methods, Create, CreateSubAgent, and inner classes unchanged ...
}
```

Note: Keep `_registryOptions` and `ConfigureAgents` only if integration tests call them. If they don't call them after this refactoring, remove them. Verify with `grep -rn "ConfigureAgents" Tests/Integration/`.

- [ ] **Step 2: Remove definition-management methods from `FakeAgentFactory` in `MonitorTests.cs`**

In `Tests/Unit/Domain/MonitorTests.cs`, the `FakeAgentFactory` class at line 80 implements `IAgentFactory`. Remove the three methods that are no longer on the interface:

Replace lines 80-99 with:

```csharp
internal sealed class FakeAgentFactory(DisposableAgent agent) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
    {
        return agent;
    }

    public DisposableAgent CreateSubAgent(SubAgentDefinition definition, IToolApprovalHandler approvalHandler, string[] whitelistPatterns, string userId)
        => throw new NotImplementedException();
}
```

Remove unused usings: `Domain.DTOs.WebChat` (if the only usage was `CustomAgentRegistration`/`AgentInfo` in the removed methods).

- [ ] **Step 3: Verify build compiles**

Run: `dotnet build Tests/Tests.csproj 2>&1 | tail -10`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Tests/Integration/Fixtures/FakeAgentFactory.cs Tests/Unit/Domain/MonitorTests.cs
git commit -m "refactor: remove definition-management methods from test fakes"
```

---

### Task 8: Update `MultiAgentFactoryTests`

**Files:**
- Modify: `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs`

- [ ] **Step 1: Remove registration/query tests and update factory construction**

The registration/query tests have been moved to `AgentDefinitionProviderTests` (Task 2). The factory tests that test `Create` need updating — they previously called `RegisterCustomAgent` on the factory, but now the factory doesn't have that method. Instead, set up custom agents through the `CustomAgentRegistry` directly and wire up an `AgentDefinitionProvider`.

Replace the full file:

```csharp
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
            domainToolRegistry.Object);
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
```

- [ ] **Step 2: Run all factory tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MultiAgentFactoryTests" --no-restore 2>&1 | tail -20`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs
git commit -m "refactor: update MultiAgentFactoryTests to use provider for definitions"
```

---

### Task 9: Run Full Test Suite and Fix Any Remaining Issues

**Files:**
- Potentially any file touched in previous tasks

- [ ] **Step 1: Build the full solution**

Run: `dotnet build 2>&1 | tail -20`
Expected: Build succeeds with no errors.

- [ ] **Step 2: Run all unit and integration tests**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" 2>&1 | tail -30`
Expected: All tests pass.

- [ ] **Step 3: Fix any failures**

If any tests fail, investigate and fix. Common issues:
- Other test files that mock `IAgentFactory` may still reference the removed methods
- DI registration order issues

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "refactor: complete migration of registration logic to AgentDefinitionProvider"
```
