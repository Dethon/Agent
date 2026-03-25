# Subagent Feature Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable the parent agent to spawn a child agent with a different system prompt and fresh context to handle a subtask synchronously and return the result, keeping the parent's context small.

**Architecture:** Follows the existing domain tool pattern (like scheduling). A `SubAgentRunTool` domain tool calls `ISubAgentRunner` (Domain contract, implemented in Infrastructure) which creates a fresh `McpAgent` via the existing factory infrastructure, runs a single prompt, and returns the result. Parent context (approval handler, whitelist, userId) is passed via an `ISubAgentContextAccessor` backed by a `ConcurrentDictionary` keyed by agent name. Resource subscriptions are disabled for subagents via a new flag on `ThreadSessionBuilder`/`McpAgent`.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, MCP, OpenRouter, Redis (Testcontainers for tests), xUnit, Shouldly

**Spec:** `docs/superpowers/specs/2026-03-25-subagent-feature-design.md`

---

### Task 1: NullThreadStateStore

**Files:**
- Create: `Infrastructure/StateManagers/NullThreadStateStore.cs`
- Test: `Tests/Unit/Infrastructure/NullThreadStateStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Domain.Agents;
using Domain.DTOs.WebChat;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class NullThreadStateStoreTests
{
    private readonly NullThreadStateStore _store = new();

    [Fact]
    public async Task GetMessagesAsync_ReturnsNull()
    {
        var result = await _store.GetMessagesAsync("any-key");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SetMessagesAsync_DoesNotThrow()
    {
        await _store.SetMessagesAsync("key", [new ChatMessage(ChatRole.User, "hi")]);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow()
    {
        await _store.DeleteAsync(new AgentKey("test"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse()
    {
        var result = await _store.ExistsAsync("key");
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAllTopicsAsync_ReturnsEmpty()
    {
        var result = await _store.GetAllTopicsAsync("agent-id");
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveTopicAsync_DoesNotThrow()
    {
        var topic = new TopicMetadata
        {
            AgentId = "a", ChatId = 1, TopicId = "t", Title = "test",
            CreatedAt = DateTime.UtcNow
        };
        await _store.SaveTopicAsync(topic);
    }

    [Fact]
    public async Task DeleteTopicAsync_DoesNotThrow()
    {
        await _store.DeleteTopicAsync("agent", 1, "topic");
    }

    [Fact]
    public async Task GetTopicByChatIdAndThreadIdAsync_ReturnsNull()
    {
        var result = await _store.GetTopicByChatIdAndThreadIdAsync("agent", 1, 1);
        result.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~NullThreadStateStoreTests" -v m`
Expected: FAIL — `NullThreadStateStore` class does not exist

- [ ] **Step 3: Write minimal implementation**

```csharp
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Infrastructure.StateManagers;

public sealed class NullThreadStateStore : IThreadStateStore
{
    public Task<ChatMessage[]?> GetMessagesAsync(string key) => Task.FromResult<ChatMessage[]?>(null);

    public Task SetMessagesAsync(string key, ChatMessage[] messages) => Task.CompletedTask;

    public Task DeleteAsync(AgentKey key) => Task.CompletedTask;

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(false);

    public Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string? spaceSlug = null)
        => Task.FromResult<IReadOnlyList<TopicMetadata>>([]);

    public Task SaveTopicAsync(TopicMetadata topic) => Task.CompletedTask;

    public Task DeleteTopicAsync(string agentId, long chatId, string topicId) => Task.CompletedTask;

    public Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(
        string agentId, long chatId, long threadId, CancellationToken ct = default)
        => Task.FromResult<TopicMetadata?>(null);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~NullThreadStateStoreTests" -v m`
Expected: All 8 tests PASS

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/StateManagers/NullThreadStateStore.cs Tests/Unit/Infrastructure/NullThreadStateStoreTests.cs
git commit -m "feat: add NullThreadStateStore for ephemeral subagent conversations"
```

---

### Task 2: Disable Resource Subscriptions in ThreadSessionBuilder and McpAgent

**Files:**
- Modify: `Infrastructure/Agents/ThreadSession.cs` (ThreadSessionData, ThreadSession, ThreadSessionBuilder)
- Modify: `Infrastructure/Agents/McpAgent.cs`

No new tests — existing `McpAgentIntegrationTests` validate the default (enabled) path. Integration tests in Task 9 cover the disabled path.

- [ ] **Step 1: Make `ThreadSessionData.ResourceManager` nullable**

In `Infrastructure/Agents/ThreadSession.cs`, change the record:

```csharp
internal sealed record ThreadSessionData(
    McpClientManager ClientManager,
    McpResourceManager? ResourceManager,
    IReadOnlyList<AITool> Tools);
```

- [ ] **Step 2: Update `ThreadSession.ResourceManager` property type to nullable**

```csharp
public McpResourceManager? ResourceManager => _data.ResourceManager;
```

- [ ] **Step 3: Add `enableResourceSubscriptions` parameter to `ThreadSession.CreateAsync`**

```csharp
public static async Task<ThreadSession> CreateAsync(
    string[] endpoints,
    string name,
    string userId,
    string description,
    ChatClientAgent agent,
    AgentSession thread,
    IReadOnlyList<AIFunction> domainTools,
    CancellationToken ct,
    bool enableResourceSubscriptions = true)
{
    var builder = new ThreadSessionBuilder(endpoints, name, description, agent, thread, userId, domainTools);
    var data = await builder.BuildAsync(ct, enableResourceSubscriptions);
    return new ThreadSession(data);
}
```

- [ ] **Step 4: Add flag to `ThreadSessionBuilder.BuildAsync`**

```csharp
public async Task<ThreadSessionData> BuildAsync(CancellationToken ct, bool enableResourceSubscriptions = true)
{
    var samplingHandler = new McpSamplingHandler(agent, () => _tools);
    var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

    var clientManager = await McpClientManager.CreateAsync(name, userId, description, endpoints, handlers, ct);

    _tools = clientManager.Tools.Concat(domainTools).ToList();

    McpResourceManager? resourceManager = enableResourceSubscriptions
        ? await CreateResourceManagerAsync(clientManager, ct)
        : null;

    return new ThreadSessionData(clientManager, resourceManager, _tools);
}
```

- [ ] **Step 5: Null-guard `ThreadSession.DisposeAsync`**

```csharp
public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
    {
        return;
    }

    if (_data.ResourceManager is not null)
    {
        await _data.ResourceManager.DisposeAsync();
    }

    await _data.ClientManager.DisposeAsync();
}
```

- [ ] **Step 6: Add `enableResourceSubscriptions` field to `McpAgent` constructor**

In `Infrastructure/Agents/McpAgent.cs`, add a new field and constructor parameter:

```csharp
private readonly bool _enableResourceSubscriptions;

public McpAgent(
    string[] endpoints,
    IChatClient chatClient,
    string name,
    string description,
    IThreadStateStore stateStore,
    string userId,
    string? customInstructions = null,
    IReadOnlyList<AIFunction>? domainTools = null,
    bool enableResourceSubscriptions = true)
{
    // ... existing assignments ...
    _enableResourceSubscriptions = enableResourceSubscriptions;
}
```

- [ ] **Step 7: Update `McpAgent.RunCoreStreamingAsync` to skip resource logic when disabled**

```csharp
protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession? thread = null,
    AgentRunOptions? options = null,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
    thread ??= await CreateSessionAsync(cancellationToken);
    var session = await GetOrCreateSessionAsync(thread, cancellationToken);

    if (session.ResourceManager is not null)
    {
        await session.ResourceManager.EnsureChannelActive(cancellationToken);
    }

    options ??= CreateRunOptions(session);

    if (session.ResourceManager is null)
    {
        await foreach (var update in _innerAgent.RunStreamingAsync(messages, thread, options, cancellationToken))
        {
            yield return update;
        }
        yield break;
    }

    var mainResponses = RunStreamingCoreAsync(messages, thread, session, options, cancellationToken);
    var notificationResponses = session.ResourceManager.SubscriptionChannel.Reader.ReadAllAsync(cancellationToken);

    await foreach (var update in mainResponses.Merge(notificationResponses, cancellationToken))
    {
        yield return update;
    }

    await foreach (var update in session.ResourceManager.SubscriptionChannel.Reader.ReadAllAsync(cancellationToken))
    {
        yield return update;
    }
}
```

- [ ] **Step 8: Update `McpAgent.RunStreamingCoreAsync` to skip sync when no resource manager**

```csharp
private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCoreAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession thread,
    ThreadSession session,
    AgentRunOptions options,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
    await foreach (var update in _innerAgent.RunStreamingAsync(messages, thread, options, ct))
    {
        yield return update;
    }

    if (session.ResourceManager is not null)
    {
        await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, ct);
    }
}
```

- [ ] **Step 9: Forward flag in `GetOrCreateSessionAsync`**

```csharp
private async Task<ThreadSession> GetOrCreateSessionAsync(AgentSession thread, CancellationToken ct)
{
    return await _syncLock.WithLockAsync(async () =>
    {
        if (_threadSessions.TryGetValue(thread, out var existing))
        {
            return existing;
        }

        var newSession = await ThreadSession
            .CreateAsync(_endpoints, _name, _userId, _description, _innerAgent,
                         thread, _domainTools, ct, _enableResourceSubscriptions);
        _threadSessions[thread] = newSession;
        return newSession;
    }, ct);
}
```

- [ ] **Step 10: Verify build**

Run: `dotnet build`
Expected: Build succeeds (all callers use default `enableResourceSubscriptions = true`)

- [ ] **Step 11: Commit**

```bash
git add Infrastructure/Agents/ThreadSession.cs Infrastructure/Agents/McpAgent.cs
git commit -m "feat: add flag to disable resource subscriptions for subagent use"
```

---

### Task 3: Domain DTOs and Contracts

**Files:**
- Create: `Domain/DTOs/SubAgentDefinition.cs`
- Create: `Domain/DTOs/SubAgentRegistryOptions.cs`
- Create: `Domain/DTOs/SubAgentContext.cs`
- Create: `Domain/Contracts/ISubAgentRunner.cs`
- Create: `Domain/Contracts/ISubAgentContextAccessor.cs`

No tests — pure data types and interfaces.

- [ ] **Step 1: Create `SubAgentDefinition`**

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record SubAgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string? CustomInstructions { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
    public int MaxExecutionSeconds { get; init; } = 120;
}
```

- [ ] **Step 2: Create `SubAgentRegistryOptions`**

```csharp
namespace Domain.DTOs;

public sealed class SubAgentRegistryOptions
{
    public SubAgentDefinition[] SubAgents { get; set; } = [];
}
```

- [ ] **Step 3: Create `SubAgentContext`**

```csharp
using Domain.Contracts;

namespace Domain.DTOs;

public record SubAgentContext(
    IToolApprovalHandler ApprovalHandler,
    string[] WhitelistPatterns,
    string UserId);
```

- [ ] **Step 4: Create `ISubAgentRunner`**

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface ISubAgentRunner
{
    Task<string> RunAsync(
        SubAgentDefinition definition,
        string prompt,
        SubAgentContext parentContext,
        CancellationToken ct = default);
}
```

- [ ] **Step 5: Create `ISubAgentContextAccessor`**

Uses `ConcurrentDictionary` keyed by agent name. This is more reliable than `AsyncLocal` because the context is set at agent creation time but consumed during the LLM tool loop, which may run in a different async context.

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface ISubAgentContextAccessor
{
    void SetContext(string agentName, SubAgentContext context);
    SubAgentContext? GetContext(string agentName);
    void RemoveContext(string agentName);
}
```

- [ ] **Step 6: Verify build**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 7: Commit**

```bash
git add Domain/DTOs/SubAgentDefinition.cs Domain/DTOs/SubAgentRegistryOptions.cs Domain/DTOs/SubAgentContext.cs Domain/Contracts/ISubAgentRunner.cs Domain/Contracts/ISubAgentContextAccessor.cs
git commit -m "feat: add subagent domain DTOs and contracts"
```

---

### Task 4: SubAgentContextAccessor Implementation

**Files:**
- Create: `Infrastructure/Agents/SubAgentContextAccessor.cs`

No tests — trivial `ConcurrentDictionary` wrapper. Covered by integration tests in Task 9.

- [ ] **Step 1: Create `SubAgentContextAccessor`**

```csharp
using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Agents;

public sealed class SubAgentContextAccessor : ISubAgentContextAccessor
{
    private readonly ConcurrentDictionary<string, SubAgentContext> _contexts = new();

    public void SetContext(string agentName, SubAgentContext context) =>
        _contexts[agentName] = context;

    public SubAgentContext? GetContext(string agentName) =>
        _contexts.GetValueOrDefault(agentName);

    public void RemoveContext(string agentName) =>
        _contexts.TryRemove(agentName, out _);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add Infrastructure/Agents/SubAgentContextAccessor.cs
git commit -m "feat: add SubAgentContextAccessor for parent context passing"
```

---

### Task 5: SubAgentRunner Implementation

**Files:**
- Create: `Infrastructure/Agents/SubAgentRunner.cs`

No isolated tests — covered by integration tests in Task 9.

- [ ] **Step 1: Create `SubAgentRunner`**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public sealed class SubAgentRunner(
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null) : ISubAgentRunner
{
    public async Task<string> RunAsync(
        SubAgentDefinition definition,
        string prompt,
        SubAgentContext parentContext,
        CancellationToken ct = default)
    {
        var agentPublisher = metricsPublisher is not null
            ? new AgentMetricsPublisher(metricsPublisher, definition.Id)
            : null;

        var chatClient = new OpenRouterChatClient(
            openRouterConfig.ApiUrl,
            openRouterConfig.ApiKey,
            definition.Model,
            agentPublisher);

        var effectiveClient = new ToolApprovalChatClient(
            chatClient,
            parentContext.ApprovalHandler,
            parentContext.WhitelistPatterns,
            agentPublisher);

        var enabledFeatures = definition.EnabledFeatures
            .Where(f => !f.Equals("subagents", StringComparison.OrdinalIgnoreCase));

        var domainTools = domainToolRegistry
            .GetToolsForFeatures(enabledFeatures)
            .ToList();

        await using var agent = new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            $"subagent-{definition.Id}",
            definition.Description ?? "",
            new NullThreadStateStore(),
            parentContext.UserId,
            definition.CustomInstructions,
            domainTools,
            enableResourceSubscriptions: false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(definition.MaxExecutionSeconds));

        var userMessage = new ChatMessage(ChatRole.User, prompt);
        var response = await agent.RunStreamingAsync(
                [userMessage], cancellationToken: timeoutCts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(timeoutCts.Token);

        return string.Join("", response.Select(r => r.Content).Where(c => !string.IsNullOrEmpty(c)));
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 3: Commit**

```bash
git add Infrastructure/Agents/SubAgentRunner.cs
git commit -m "feat: add SubAgentRunner to create and execute ephemeral subagents"
```

---

### Task 6: SubAgentRunTool and SubAgentToolFeature

**Files:**
- Create: `Domain/Tools/SubAgents/SubAgentRunTool.cs`
- Create: `Domain/Tools/SubAgents/SubAgentToolFeature.cs`
- Test: `Tests/Unit/Domain/SubAgents/SubAgentRunToolTests.cs`

The `SubAgentRunTool` needs the parent agent's name to look up its context from `ISubAgentContextAccessor`. The agent name is not available as a DI dependency — it's known at agent creation time. The `SubAgentToolFeature` solves this by creating the `AIFunction` with a wrapper lambda that captures `agentName` at registration time.

However, domain tools are created once via `DomainToolRegistry` before any specific agent exists. The tool needs a different approach: the `SubAgentRunTool.RunAsync` takes `agentName` as a parameter. The `SubAgentToolFeature` creates a wrapper `AIFunction` that hides `agentName` from the LLM schema (the LLM never sees or fills this parameter). Instead, the tool is registered per-agent with the name captured in a closure.

Simpler approach: since `DomainToolRegistry` creates tools at agent creation time via `GetToolsForFeatures`, and the agent name is not yet known at that point, we need the tool to accept `agentName` as a visible parameter. The LLM will see it but won't know what to fill in. This is unclean.

**Cleanest approach:** `SubAgentRunTool` stores a mutable `AgentName` property. `MultiAgentFactory` sets it after creating the domain tools. Since domain tools are transient (new instance per `GetToolsForFeatures` call), each agent gets its own tool instance.

Wait — `IDomainToolFeature` is registered as transient, and `DomainToolRegistry` calls `GetToolsForFeatures` which calls `feature.GetTools()`. The `AIFunction` is created from the tool's method at that point. If the tool is transient, each call to `GetToolsForFeatures` produces a new tool instance. So `MultiAgentFactory` can set the `AgentName` on the tool after getting it from the registry... but the `AIFunction` is already bound to the method.

**Simplest working approach:** `SubAgentRunTool` reads the agent name from the accessor's keys. Since there's typically only one active parent agent per tool invocation, the accessor can expose a method that returns the first/only context. But with multiple agents this breaks.

**Final approach:** Keep `agentName` as a parameter on `RunAsync` but exclude it from the LLM-visible schema. Use `AIFunctionFactory.Create` with a lambda wrapper in `SubAgentToolFeature` that doesn't expose `agentName`. The `SubAgentToolFeature` receives the agent name at construction time. Since `SubAgentToolFeature` is transient via DI, each agent gets its own instance... but the agent name isn't known at DI resolution time.

**Pragmatic solution:** Make the tool take `agentName` as a regular parameter. The LLM might try to fill it, but since it's not described, most LLMs will skip it. If it causes issues, we can wrap it later. For now, the `SubAgentToolFeature` creates the AIFunction normally and includes `agentName` in the schema. The parent agent's system prompt can mention the agent name.

Actually, the much simpler solution: **don't use agent name at all**. The `SubAgentContextAccessor` stores contexts keyed by agent name, but we can key by the current thread/conversation ID instead. Or even simpler: since `McpAgent` names are unique (they include the conversation ID), and `MultiAgentFactory.CreateFromDefinition` creates a unique name per call, we can pass the accessor reference directly into the tool at tool creation time.

**Simplest correct solution:** The `SubAgentRunTool` constructor receives the `ISubAgentContextAccessor`. `MultiAgentFactory.CreateFromDefinition` sets context on the accessor keyed by agent name. The tool just needs to know which agent name to look up. Since the tool runs inside the agent's LLM loop, we can pass the agent name through the tool's constructor by making `SubAgentToolFeature` a factory that accepts the agent name. But DI doesn't know the agent name...

**Let's go with the most pragmatic approach:** Include `agentName` as a hidden parameter. The `SubAgentToolFeature` creates the AIFunction from a wrapper delegate that auto-fills `agentName`. But the feature doesn't know the name...

**OK, final decision:** Use a simple pattern — make `ISubAgentContextAccessor` thread-keyed using the managed thread ID. When `MultiAgentFactory.CreateFromDefinition` is called, it captures the calling context. When the tool runs (during the same agent's LLM loop, same managed thread or continuation), it looks up by the same key.

Actually this is getting overcomplicated. Let's just do what the spec originally said: `ConcurrentDictionary` keyed by agent name, and the tool takes `agentName` as a parameter with a description that says "Your agent name - pass your own name". The LLM will fill it from the system prompt which includes the agent name. This is simple, works, and matches how other tools handle context.

- [ ] **Step 1: Write failing tests for SubAgentRunTool**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.SubAgents;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.SubAgents;

public class SubAgentRunToolTests
{
    private readonly Mock<ISubAgentRunner> _runner = new();
    private readonly Mock<ISubAgentContextAccessor> _contextAccessor = new();

    private static readonly SubAgentDefinition TestProfile = new()
    {
        Id = "summarizer",
        Name = "Summarizer",
        Description = "Summarizes content",
        Model = "test-model",
        McpServerEndpoints = []
    };

    private SubAgentRunTool CreateTool(params SubAgentDefinition[] profiles) =>
        new(_runner.Object, _contextAccessor.Object,
            Options.Create(new SubAgentRegistryOptions { SubAgents = profiles }));

    [Fact]
    public async Task RunAsync_UnknownProfile_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.RunAsync("unknown", "do something", "parent");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("unknown");
    }

    [Fact]
    public async Task RunAsync_MissingContext_ReturnsError()
    {
        _contextAccessor.Setup(a => a.GetContext("parent")).Returns((SubAgentContext?)null);
        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "do something", "parent");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("context");
    }

    [Fact]
    public async Task RunAsync_ValidProfile_CallsRunnerAndReturnsResult()
    {
        var context = new SubAgentContext(
            Mock.Of<IToolApprovalHandler>(), ["pattern:*"], "user-1");
        _contextAccessor.Setup(a => a.GetContext("parent")).Returns(context);
        _runner.Setup(r => r.RunAsync(TestProfile, "summarize this", context, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Summary result");

        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "summarize this", "parent");

        result["status"]!.GetValue<string>().ShouldBe("completed");
        result["result"]!.GetValue<string>().ShouldBe("Summary result");
    }

    [Fact]
    public async Task RunAsync_RunnerThrows_ReturnsError()
    {
        var context = new SubAgentContext(
            Mock.Of<IToolApprovalHandler>(), [], "user-1");
        _contextAccessor.Setup(a => a.GetContext("parent")).Returns(context);
        _runner.Setup(r => r.RunAsync(It.IsAny<SubAgentDefinition>(), It.IsAny<string>(),
                It.IsAny<SubAgentContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("timed out"));

        var tool = CreateTool(TestProfile);

        var result = await tool.RunAsync("summarizer", "do something", "parent");

        result["status"]!.GetValue<string>().ShouldBe("error");
        result["error"]!.GetValue<string>().ShouldContain("timed out");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~SubAgentRunToolTests" -v m`
Expected: FAIL — `SubAgentRunTool` class does not exist

- [ ] **Step 3: Write `SubAgentRunTool`**

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Options;

namespace Domain.Tools.SubAgents;

public class SubAgentRunTool(
    ISubAgentRunner runner,
    ISubAgentContextAccessor contextAccessor,
    IOptions<SubAgentRegistryOptions> registryOptions)
{
    public const string Name = "run_subagent";

    private readonly SubAgentDefinition[] _profiles = registryOptions.Value.SubAgents;

    public string Description
    {
        get
        {
            var profileList = string.Join("\n",
                _profiles.Select(p => $"- \"{p.Id}\": {p.Description ?? p.Name}"));
            return $"""
                    Runs a task on a subagent with a fresh context and returns the result.
                    Available subagents:
                    {profileList}
                    """;
        }
    }

    [Description("Runs a task on a subagent with a fresh context and returns the result.")]
    public async Task<JsonNode> RunAsync(
        [Description("ID of the subagent profile to use")]
        string subAgentId,
        [Description("The task/prompt to send to the subagent")]
        string prompt,
        [Description("Your own agent name (from your system prompt)")]
        string agentName,
        CancellationToken ct = default)
    {
        var profile = _profiles.FirstOrDefault(p =>
            p.Id.Equals(subAgentId, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = $"Unknown subagent: '{subAgentId}'. Available: {string.Join(", ", _profiles.Select(p => p.Id))}"
            };
        }

        var context = contextAccessor.GetContext(agentName);
        if (context is null)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = "Subagent context not available for this agent"
            };
        }

        try
        {
            var result = await runner.RunAsync(profile, prompt, context, ct);
            return new JsonObject
            {
                ["status"] = "completed",
                ["result"] = result
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = ex.Message
            };
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~SubAgentRunToolTests" -v m`
Expected: All 4 tests PASS

- [ ] **Step 5: Write `SubAgentToolFeature`**

```csharp
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentToolFeature(SubAgentRunTool runTool) : IDomainToolFeature
{
    private const string Feature = "subagents";

    public string FeatureName => Feature;

    public IEnumerable<AIFunction> GetTools()
    {
        yield return AIFunctionFactory.Create(
            runTool.RunAsync,
            name: $"domain:{Feature}:{SubAgentRunTool.Name}",
            description: runTool.Description);
    }
}
```

- [ ] **Step 6: Verify build**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 7: Commit**

```bash
git add Domain/Tools/SubAgents/ Tests/Unit/Domain/SubAgents/
git commit -m "feat: add SubAgentRunTool and SubAgentToolFeature domain tools"
```

---

### Task 7: MultiAgentFactory — Set SubAgentContext

**Files:**
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`
- Modify: `Agent/Modules/InjectorModule.cs`

The factory sets `ISubAgentContextAccessor` context keyed by agent name when creating an agent with `"subagents"` in its enabled features.

- [ ] **Step 1: Add `ISubAgentContextAccessor` to `MultiAgentFactory` constructor**

```csharp
public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry,
    IMetricsPublisher? metricsPublisher = null,
    ISubAgentContextAccessor? subAgentContextAccessor = null) : IAgentFactory, IScheduleAgentFactory
```

Add `using Domain.Contracts;` if not already imported (for `ISubAgentContextAccessor`).

- [ ] **Step 2: Set context in `CreateFromDefinition` when subagents feature is enabled**

After creating the agent name but before returning, set the context:

```csharp
public DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
{
    var agentPublisher = metricsPublisher is not null
        ? new AgentMetricsPublisher(metricsPublisher, definition.Id)
        : metricsPublisher;
    var chatClient = CreateChatClient(definition.Model, agentPublisher);
    var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

    var name = $"{definition.Name}-{agentKey.ConversationId}";
    var effectiveClient = new ToolApprovalChatClient(chatClient, approvalHandler, definition.WhitelistPatterns, agentPublisher);

    var domainTools = domainToolRegistry
        .GetToolsForFeatures(definition.EnabledFeatures)
        .ToList();

    if (subAgentContextAccessor is not null &&
        definition.EnabledFeatures.Any(f => f.Equals("subagents", StringComparison.OrdinalIgnoreCase)))
    {
        subAgentContextAccessor.SetContext(name, new SubAgentContext(
            approvalHandler, definition.WhitelistPatterns, userId));
    }

    return new McpAgent(
        definition.McpServerEndpoints,
        effectiveClient,
        name,
        definition.Description ?? "",
        stateStore,
        userId,
        definition.CustomInstructions,
        domainTools);
}
```

- [ ] **Step 3: Update `InjectorModule` to pass accessor to factory**

In `Agent/Modules/InjectorModule.cs`, update the `MultiAgentFactory` construction:

```csharp
.AddSingleton<IAgentFactory>(sp =>
    new MultiAgentFactory(
        sp,
        sp.GetRequiredService<IOptionsMonitor<AgentRegistryOptions>>(),
        llmConfig,
        sp.GetRequiredService<IDomainToolRegistry>(),
        sp.GetRequiredService<IMetricsPublisher>(),
        sp.GetService<ISubAgentContextAccessor>()))
```

Add `using Domain.Contracts;` if not already imported.

- [ ] **Step 4: Verify build**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/MultiAgentFactory.cs Agent/Modules/InjectorModule.cs
git commit -m "feat: set subagent context in MultiAgentFactory when subagents feature enabled"
```

---

### Task 8: SubAgentModule — DI Registration and Config

**Files:**
- Create: `Agent/Modules/SubAgentModule.cs`
- Modify: `Agent/Modules/ConfigModule.cs`
- Modify: `Agent/Settings/AgentSettings.cs`
- Modify: `Agent/appsettings.json`
- Modify: `Agent/Modules/InjectorModule.cs`

- [ ] **Step 1: Add `SubAgents` to `AgentSettings`**

In `Agent/Settings/AgentSettings.cs`, add:

```csharp
public SubAgentDefinition[] SubAgents { get; init; } = [];
```

Ensure `using Domain.DTOs;` is present.

- [ ] **Step 2: Create `SubAgentModule`**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;

namespace Agent.Modules;

public static class SubAgentModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSubAgents(SubAgentDefinition[] subAgentDefinitions)
        {
            var hasRecursion = subAgentDefinitions.Any(d =>
                d.EnabledFeatures.Any(f => f.Equals("subagents", StringComparison.OrdinalIgnoreCase)));

            if (hasRecursion)
            {
                throw new InvalidOperationException(
                    "SubAgent definitions must not include 'subagents' in enabledFeatures. Recursive subagents are not supported.");
            }

            services.Configure<SubAgentRegistryOptions>(options =>
                options.SubAgents = subAgentDefinitions);

            services.AddSingleton<ISubAgentContextAccessor, SubAgentContextAccessor>();
            services.AddTransient<ISubAgentRunner, SubAgentRunner>();
            services.AddTransient<SubAgentRunTool>();
            services.AddTransient<IDomainToolFeature, SubAgentToolFeature>();

            return services;
        }
    }
}
```

- [ ] **Step 3: Register `OpenRouterConfig` as a singleton in DI**

In `Agent/Modules/InjectorModule.cs`, add before the `MultiAgentFactory` registration:

```csharp
.AddSingleton(llmConfig)
```

This makes `OpenRouterConfig` available for `SubAgentRunner`'s DI constructor injection.

- [ ] **Step 4: Wire into `ConfigModule`**

In `Agent/Modules/ConfigModule.cs`, update `ConfigureAgents`:

```csharp
public static IServiceCollection ConfigureAgents(
    this IServiceCollection services, AgentSettings settings, CommandLineParams cmdParams)
{
    return services
        .AddAgent(settings)
        .AddScheduling()
        .AddSubAgents(settings.SubAgents)
        .AddChatMonitoring(settings, cmdParams);
}
```

- [ ] **Step 5: Add empty subagent config to appsettings.json**

In `Agent/appsettings.json`, add after the `agents` array closing bracket:

```json
"subAgents": [],
```

- [ ] **Step 6: Verify build**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 7: Commit**

```bash
git add Agent/Modules/SubAgentModule.cs Agent/Modules/ConfigModule.cs Agent/Settings/AgentSettings.cs Agent/appsettings.json Agent/Modules/InjectorModule.cs
git commit -m "feat: add SubAgentModule DI registration and config binding"
```

---

### Task 9: Integration Tests

**Files:**
- Create: `Tests/Integration/Agents/SubAgentIntegrationTests.cs`

These tests use real Redis (via `RedisFixture`), real OpenRouter LLM calls (via user secrets), and the full agent infrastructure. Check `Domain/DTOs/ToolApprovalResult.cs` — it's an enum (`Rejected`, `Approved`, `ApprovedAndRemember`, `AutoApproved`), not a class.

- [ ] **Step 1: Write `SubAgentIntegrationTests`**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class SubAgentIntegrationTests(RedisFixture redisFixture)
    : IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<SubAgentIntegrationTests>()
        .Build();

    private static OpenRouterChatClient CreateLlmClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenRouterChatClient(apiUrl, apiKey, "google/gemini-2.5-flash");
    }

    private static OpenRouterConfig CreateOpenRouterConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenRouterConfig { ApiUrl = apiUrl, ApiKey = apiKey };
    }

    [SkippableFact]
    public async Task SubAgent_CompletesTask_ReturnsResult()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "echo-agent",
            Name = "Echo",
            Description = "Echoes back what you say",
            Model = "google/gemini-2.5-flash",
            McpServerEndpoints = [],
            CustomInstructions = "You are a simple echo agent. Repeat back exactly what the user says, nothing more."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var domainToolRegistry = new DomainToolRegistry([]);
        var runner = new SubAgentRunner(openRouterConfig, domainToolRegistry);
        var contextAccessor = new SubAgentContextAccessor();
        var runTool = new SubAgentRunTool(
            runner, contextAccessor,
            Options.Create(new SubAgentRegistryOptions { SubAgents = [subAgentDef] }));
        var toolFeature = new SubAgentToolFeature(runTool);

        var approvalHandler = new AutoApproveHandler();
        var agentName = "parent-agent-test";
        contextAccessor.SetContext(agentName, new SubAgentContext(approvalHandler, ["domain:subagents:*"], "test-user"));

        var llmClient = CreateLlmClient();
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(5));
        var effectiveClient = new ToolApprovalChatClient(llmClient, approvalHandler, ["domain:subagents:*"]);

        var agent = new McpAgent(
            [],
            effectiveClient,
            agentName,
            "",
            stateStore,
            "test-user",
            $"Your agent name is '{agentName}'. You have access to a subagent tool. Use the echo-agent subagent to echo back: 'Hello from subagent'",
            toolFeature.GetTools().ToList());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var responses = await agent.RunStreamingAsync(
                "Use the run_subagent tool with echo-agent to echo: 'Hello from subagent'. Pass your agent name as the agentName parameter.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        responses.ShouldNotBeEmpty();
        var combined = string.Join(" ", responses.Select(r => r.Content).Where(c => !string.IsNullOrEmpty(c)));
        combined.ShouldNotBeNullOrEmpty();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task SubAgent_EphemeralState_NoRedisKeys()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "test-ephemeral",
            Name = "TestEphemeral",
            Model = "google/gemini-2.5-flash",
            McpServerEndpoints = [],
            CustomInstructions = "Reply with exactly the word 'done'."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var runner = new SubAgentRunner(openRouterConfig, new DomainToolRegistry([]));
        var approvalHandler = new AutoApproveHandler();
        var context = new SubAgentContext(approvalHandler, [], "test-user");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = redisFixture.Connection.GetServer(redisFixture.Connection.GetEndPoints()[0]);
        var keysBefore = server.Keys(pattern: "subagent-test-ephemeral*").ToList();

        var result = await runner.RunAsync(subAgentDef, "Say done", context, cts.Token);

        var keysAfter = server.Keys(pattern: "subagent-test-ephemeral*").ToList();
        keysAfter.Count.ShouldBe(keysBefore.Count);
        result.ShouldNotBeNullOrEmpty();
    }
}

file sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
```

Note: `ToolApprovalResult` is an enum — use `ToolApprovalResult.Approved`, not `new ToolApprovalResult(true)`. Check exact `ToolApprovalRequest` type in `Domain/DTOs/ToolApprovalRequest.cs` and adjust imports.

- [ ] **Step 2: Run integration tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~SubAgentIntegrationTests" -v m`
Expected: Tests PASS (or SKIP if API key not configured)

- [ ] **Step 3: Fix any issues discovered during integration testing**

Integration tests often reveal wiring issues. Fix and re-run until green.

- [ ] **Step 4: Commit**

```bash
git add Tests/Integration/Agents/SubAgentIntegrationTests.cs
git commit -m "test: add subagent integration tests with real LLM and Redis"
```

---

### Task 10: Final Verification

- [ ] **Step 1: Run full test suite**

Run: `dotnet test Tests/ -v m`
Expected: All existing tests still pass, new tests pass (or skip gracefully)

- [ ] **Step 2: Run build**

Run: `dotnet build`
Expected: Clean build, no warnings related to new code

- [ ] **Step 3: Commit any final fixes if needed**
