# Domain Tool Features Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable domain scheduling tools to be callable by agents during conversations through AIFunction wrappers with configuration-based opt-in.

**Architecture:** Feature provider pattern where each domain tool set implements `IDomainToolFeature`. A `DomainToolRegistry` aggregates providers and filters by `AgentDefinition.EnabledFeatures`. Tools are injected into agents alongside MCP tools at creation time.

**Tech Stack:** Microsoft.Extensions.AI (AIFunctionFactory), System.ComponentModel (Description attributes)

---

### Task 1: Add Microsoft.Extensions.AI.Abstractions to Domain

**Files:**
- Modify: `Domain/Domain.csproj`

**Step 1: Add package reference**

In `Domain/Domain.csproj`, add within the `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.5.0" />
```

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Domain.csproj
git commit -m "chore: add Microsoft.Extensions.AI.Abstractions to Domain"
```

---

### Task 2: Add EnabledFeatures to AgentDefinition

**Files:**
- Modify: `Domain/DTOs/AgentDefinition.cs`

**Step 1: Add property**

In `Domain/DTOs/AgentDefinition.cs`, add after `TelegramBotToken`:

```csharp
public string[] EnabledFeatures { get; init; } = [];
```

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/DTOs/AgentDefinition.cs
git commit -m "feat: add EnabledFeatures to AgentDefinition"
```

---

### Task 3: Create IDomainToolFeature Contract

**Files:**
- Create: `Domain/Contracts/IDomainToolFeature.cs`

**Step 1: Create interface**

```csharp
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolFeature
{
    string FeatureName { get; }
    IEnumerable<AIFunction> GetTools();
}
```

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Contracts/IDomainToolFeature.cs
git commit -m "feat: add IDomainToolFeature contract"
```

---

### Task 4: Create IDomainToolRegistry Contract

**Files:**
- Create: `Domain/Contracts/IDomainToolRegistry.cs`

**Step 1: Create interface**

```csharp
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolRegistry
{
    IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures);
}
```

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Contracts/IDomainToolRegistry.cs
git commit -m "feat: add IDomainToolRegistry contract"
```

---

### Task 5: Add Description Attributes to ScheduleCreateTool

**Files:**
- Modify: `Domain/Tools/Scheduling/ScheduleCreateTool.cs`

**Step 1: Update the tool**

Replace the entire file with:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleCreateTool(
    IScheduleStore store,
    ICronValidator cronValidator,
    IAgentDefinitionProvider agentProvider)
{
    public const string Name = "schedule_create";

    public const string Description = """
        Creates a scheduled agent task. The specified agent will run with the given prompt
        at the scheduled time(s).

        For recurring schedules, use cronExpression (standard 5-field cron format):
        - "0 9 * * *" = every day at 9:00 AM
        - "0 */2 * * *" = every 2 hours
        - "30 14 * * 1-5" = weekdays at 2:30 PM

        For one-time schedules, use runAt with a UTC datetime.

        The channel specifies where responses will be delivered (telegram or webchat).
        """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(
        [Description("Agent ID to execute the task")] string agentId,
        [Description("The prompt/task to execute")] string prompt,
        [Description("Cron expression for recurring schedules (5-field format)")] string? cronExpression = null,
        [Description("ISO 8601 datetime for one-time execution (UTC)")] DateTime? runAt = null,
        [Description("Channel to send results: 'telegram' or 'webchat'")] string channel = "telegram",
        [Description("Chat ID for the target conversation")] long? chatId = null,
        [Description("Thread ID within the chat")] long? threadId = null,
        [Description("User ID for WebChat channel")] string? userId = null,
        [Description("Target agent ID for WebChat routing")] string? targetAgentId = null,
        CancellationToken ct = default)
    {
        var validationError = Validate(agentId, cronExpression, runAt, channel);
        if (validationError is not null)
        {
            return validationError;
        }

        var agentDefinition = agentProvider.GetById(agentId);
        if (agentDefinition is null)
        {
            return new JsonObject { ["error"] = $"Agent '{agentId}' not found" };
        }

        var nextRunAt = CalculateNextRunAt(cronExpression, runAt);

        var schedule = new Schedule
        {
            Id = $"sched_{Guid.NewGuid():N}",
            Agent = agentDefinition,
            Prompt = prompt,
            CronExpression = cronExpression,
            RunAt = runAt,
            Target = new ScheduleTarget
            {
                Channel = channel,
                ChatId = chatId,
                ThreadId = threadId,
                UserId = userId,
                AgentId = targetAgentId
            },
            CreatedAt = DateTime.UtcNow,
            NextRunAt = nextRunAt
        };

        await store.CreateAsync(schedule, ct);

        return new JsonObject
        {
            ["status"] = "created",
            ["scheduleId"] = schedule.Id,
            ["agentName"] = agentDefinition.Name,
            ["nextRunAt"] = nextRunAt?.ToString("O")
        };
    }

    private JsonObject? Validate(string agentId, string? cronExpression, DateTime? runAt, string channel)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return new JsonObject { ["error"] = "agentId is required" };
        }

        if (cronExpression is null && runAt is null)
        {
            return new JsonObject { ["error"] = "Either cronExpression or runAt must be provided" };
        }

        if (cronExpression is not null && runAt is not null)
        {
            return new JsonObject { ["error"] = "Provide only cronExpression OR runAt, not both" };
        }

        if (cronExpression is not null && !cronValidator.IsValid(cronExpression))
        {
            return new JsonObject { ["error"] = $"Invalid cron expression: {cronExpression}" };
        }

        if (runAt is not null && runAt <= DateTime.UtcNow)
        {
            return new JsonObject { ["error"] = "runAt must be in the future" };
        }

        if (channel is not "telegram" and not "webchat")
        {
            return new JsonObject { ["error"] = "channel must be 'telegram' or 'webchat'" };
        }

        return null;
    }

    private DateTime? CalculateNextRunAt(string? cronExpression, DateTime? runAt)
    {
        if (runAt.HasValue)
        {
            return runAt.Value;
        }

        if (cronExpression is not null)
        {
            return cronValidator.GetNextOccurrence(cronExpression, DateTime.UtcNow);
        }

        return null;
    }
}
```

Key changes:
- Changed `protected const` to `public const` for Name and Description
- Changed `Run` to `RunAsync` (public)
- Added `[Description]` attribute to method and all parameters

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleCreateTool.cs
git commit -m "feat: add Description attributes to ScheduleCreateTool"
```

---

### Task 6: Add Description Attributes to ScheduleListTool

**Files:**
- Modify: `Domain/Tools/Scheduling/ScheduleListTool.cs`

**Step 1: Update the tool**

Replace the entire file with:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling;

public class ScheduleListTool(IScheduleStore store)
{
    public const string Name = "schedule_list";

    public const string Description = """
        Lists all scheduled agent tasks. Shows schedule ID, agent name, prompt preview,
        schedule timing (cron or one-shot), next run time, and target channel.
        """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(CancellationToken ct = default)
    {
        var schedules = await store.ListAsync(ct);

        var summaries = schedules
            .Select(s => new ScheduleSummary(
                s.Id,
                s.Agent.Name,
                TruncatePrompt(s.Prompt),
                s.CronExpression,
                s.RunAt,
                s.NextRunAt,
                s.Target.Channel))
            .ToList();

        return new JsonObject
        {
            ["count"] = summaries.Count,
            ["schedules"] = new JsonArray(summaries.Select(ToJson).ToArray())
        };
    }

    private static string TruncatePrompt(string prompt)
    {
        const int maxLength = 100;
        return prompt.Length <= maxLength ? prompt : $"{prompt[..maxLength]}...";
    }

    private static JsonNode ToJson(ScheduleSummary summary)
    {
        var node = new JsonObject
        {
            ["id"] = summary.Id,
            ["agentName"] = summary.AgentName,
            ["prompt"] = summary.Prompt,
            ["channel"] = summary.Channel
        };

        if (summary.CronExpression is not null)
        {
            node["cronExpression"] = summary.CronExpression;
        }

        if (summary.RunAt.HasValue)
        {
            node["runAt"] = summary.RunAt.Value.ToString("O");
        }

        if (summary.NextRunAt.HasValue)
        {
            node["nextRunAt"] = summary.NextRunAt.Value.ToString("O");
        }

        return node;
    }
}
```

Key changes:
- Changed `protected const` to `public const`
- Changed `Run` to `RunAsync` (public)
- Added `[Description]` attribute to method

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleListTool.cs
git commit -m "feat: add Description attributes to ScheduleListTool"
```

---

### Task 7: Add Description Attributes to ScheduleDeleteTool

**Files:**
- Modify: `Domain/Tools/Scheduling/ScheduleDeleteTool.cs`

**Step 1: Update the tool**

Replace the entire file with:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Scheduling;

public class ScheduleDeleteTool(IScheduleStore store)
{
    public const string Name = "schedule_delete";

    public const string Description = """
        Deletes a scheduled agent task by ID. Use schedule_list to find schedule IDs.
        """;

    [Description(Description)]
    public async Task<JsonNode> RunAsync(
        [Description("The schedule ID to delete (from schedule_list)")] string scheduleId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
        {
            return new JsonObject { ["error"] = "scheduleId is required" };
        }

        var existing = await store.GetAsync(scheduleId, ct);
        if (existing is null)
        {
            return new JsonObject
            {
                ["status"] = "not_found",
                ["scheduleId"] = scheduleId
            };
        }

        await store.DeleteAsync(scheduleId, ct);

        return new JsonObject
        {
            ["status"] = "deleted",
            ["scheduleId"] = scheduleId,
            ["agentName"] = existing.Agent.Name
        };
    }
}
```

Key changes:
- Changed `protected const` to `public const`
- Changed `Run` to `RunAsync` (public)
- Added `[Description]` attributes to method and parameter

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Tools/Scheduling/ScheduleDeleteTool.cs
git commit -m "feat: add Description attributes to ScheduleDeleteTool"
```

---

### Task 8: Create SchedulingToolFeature Provider

**Files:**
- Create: `Domain/Tools/Scheduling/SchedulingToolFeature.cs`

**Step 1: Create provider**

```csharp
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Scheduling;

public class SchedulingToolFeature(
    ScheduleCreateTool createTool,
    ScheduleListTool listTool,
    ScheduleDeleteTool deleteTool) : IDomainToolFeature
{
    public const string Feature = "scheduling";

    public string FeatureName => Feature;

    public IEnumerable<AIFunction> GetTools()
    {
        yield return AIFunctionFactory.Create(
            createTool.RunAsync,
            name: $"domain:{Feature}:{ScheduleCreateTool.Name}");

        yield return AIFunctionFactory.Create(
            listTool.RunAsync,
            name: $"domain:{Feature}:{ScheduleListTool.Name}");

        yield return AIFunctionFactory.Create(
            deleteTool.RunAsync,
            name: $"domain:{Feature}:{ScheduleDeleteTool.Name}");
    }
}
```

**Step 2: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Domain/Tools/Scheduling/SchedulingToolFeature.cs
git commit -m "feat: add SchedulingToolFeature provider"
```

---

### Task 9: Create DomainToolRegistry

**Files:**
- Create: `Infrastructure/Agents/DomainToolRegistry.cs`

**Step 1: Create registry**

```csharp
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public class DomainToolRegistry(IEnumerable<IDomainToolFeature> features) : IDomainToolRegistry
{
    private readonly Dictionary<string, IDomainToolFeature> _features =
        features.ToDictionary(f => f.FeatureName, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures)
    {
        return enabledFeatures
            .Where(name => _features.ContainsKey(name))
            .SelectMany(name => _features[name].GetTools());
    }
}
```

**Step 2: Verify build**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add Infrastructure/Agents/DomainToolRegistry.cs
git commit -m "feat: add DomainToolRegistry"
```

---

### Task 10: Integrate Domain Tools into McpAgent

**Files:**
- Modify: `Infrastructure/Agents/McpAgent.cs`
- Modify: `Infrastructure/Agents/ThreadSession.cs`

**Step 1: Update McpAgent constructor**

In `Infrastructure/Agents/McpAgent.cs`, add a new parameter and field:

Add after `private readonly string _userId;`:
```csharp
private readonly IReadOnlyList<AIFunction> _domainTools;
```

Update constructor to add parameter and assignment:
```csharp
public McpAgent(
    string[] endpoints,
    IChatClient chatClient,
    string name,
    string description,
    IThreadStateStore stateStore,
    string userId,
    string? customInstructions = null,
    IReadOnlyList<AIFunction>? domainTools = null)
{
    _endpoints = endpoints;
    _name = name;
    _description = description;
    _userId = userId;
    _customInstructions = customInstructions;
    _domainTools = domainTools ?? [];
    // ... rest unchanged
}
```

**Step 2: Pass domain tools to ThreadSession**

In the `GetOrCreateSessionAsync` method, update the `ThreadSession.CreateAsync` call:

```csharp
var newSession = await ThreadSession
    .CreateAsync(_endpoints, _name, _userId, _description, _innerAgent, thread, _domainTools, ct);
```

**Step 3: Update ThreadSession to accept domain tools**

In `Infrastructure/Agents/ThreadSession.cs`, update `CreateAsync`:

```csharp
public static async Task<ThreadSession> CreateAsync(
    string[] endpoints,
    string name,
    string userId,
    string description,
    ChatClientAgent agent,
    AgentThread thread,
    IReadOnlyList<AIFunction> domainTools,
    CancellationToken ct)
{
    var builder = new ThreadSessionBuilder(endpoints, name, description, agent, thread, userId, domainTools);
    var data = await builder.BuildAsync(ct);
    return new ThreadSession(data);
}
```

Update `ThreadSessionBuilder` constructor and field:

```csharp
internal sealed class ThreadSessionBuilder(
    string[] endpoints,
    string name,
    string description,
    ChatClientAgent agent,
    AgentThread thread,
    string userId,
    IReadOnlyList<AIFunction> domainTools)
{
    private IReadOnlyList<AITool> _tools = [];

    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct)
    {
        // Step 1: Create sampling handler with deferred tool access
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };

        // Step 2: Create MCP clients and load tools/prompts
        var clientManager = await McpClientManager.CreateAsync(name, userId, description, endpoints, handlers, ct);

        // Step 3: Combine MCP tools with domain tools
        _tools = clientManager.Tools.Concat(domainTools.Cast<AITool>()).ToList();

        // Step 4: Setup resource management with user context prepended
        var resourceManager = await CreateResourceManagerAsync(clientManager, ct);

        return new ThreadSessionData(clientManager, resourceManager);
    }

    private async Task<McpResourceManager> CreateResourceManagerAsync(
        McpClientManager clientManager,
        CancellationToken ct)
    {
        var instructions = string.Join("\n\n", clientManager.Prompts);
        var resourceManager = new McpResourceManager(agent, thread, instructions, _tools);

        await resourceManager.SyncResourcesAsync(clientManager.Clients, ct);
        resourceManager.SubscribeToNotifications(clientManager.Clients);

        return resourceManager;
    }
}
```

**Step 4: Verify build**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add Infrastructure/Agents/McpAgent.cs Infrastructure/Agents/ThreadSession.cs
git commit -m "feat: integrate domain tools into McpAgent"
```

---

### Task 11: Update MultiAgentFactory to Inject Domain Tools

**Files:**
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`

**Step 1: Add IDomainToolRegistry dependency**

Update the primary constructor:

```csharp
public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    OpenRouterConfig openRouterConfig,
    IDomainToolRegistry domainToolRegistry) : IAgentFactory, IScheduleAgentFactory
```

**Step 2: Update CreateFromDefinition to pass domain tools**

```csharp
public DisposableAgent CreateFromDefinition(AgentKey agentKey, string userId, AgentDefinition definition)
{
    var chatClient = CreateChatClient(definition.Model);
    var approvalHandlerFactory = serviceProvider.GetRequiredService<IToolApprovalHandlerFactory>();
    var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

    var name = $"{definition.Name}-{agentKey.ChatId}-{agentKey.ThreadId}";
    var handler = approvalHandlerFactory.Create(agentKey);
    var effectiveClient = new ToolApprovalChatClient(chatClient, handler, definition.WhitelistPatterns);

    var domainTools = domainToolRegistry
        .GetToolsForFeatures(definition.EnabledFeatures)
        .ToList();

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

**Step 3: Verify build**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add Infrastructure/Agents/MultiAgentFactory.cs
git commit -m "feat: inject domain tools via MultiAgentFactory"
```

---

### Task 12: Register DI Services

**Files:**
- Modify: `Agent/Modules/SchedulingModule.cs`
- Modify: `Agent/Modules/InjectorModule.cs`

**Step 1: Register SchedulingToolFeature in SchedulingModule**

In `Agent/Modules/SchedulingModule.cs`, add after the tool registrations:

```csharp
services.AddTransient<IDomainToolFeature, SchedulingToolFeature>();
```

Full file should be:

```csharp
using System.Threading.Channels;
using Agent.App;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Domain.Tools.Scheduling;
using Infrastructure.Agents;
using Infrastructure.StateManagers;
using Infrastructure.Validation;

namespace Agent.Modules;

public static class SchedulingModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddScheduling()
        {
            services.AddSingleton(Channel.CreateUnbounded<Schedule>(
                new UnboundedChannelOptions { SingleReader = true }));

            services.AddSingleton<IScheduleStore, RedisScheduleStore>();
            services.AddSingleton<ICronValidator, CronValidator>();
            services.AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>();

            services.AddTransient<ScheduleCreateTool>();
            services.AddTransient<ScheduleListTool>();
            services.AddTransient<ScheduleDeleteTool>();

            services.AddTransient<IDomainToolFeature, SchedulingToolFeature>();

            services.AddSingleton<ScheduleDispatcher>();
            services.AddSingleton<ScheduleExecutor>();

            services.AddHostedService<ScheduleMonitoring>();

            return services;
        }
    }
}
```

**Step 2: Register DomainToolRegistry in InjectorModule**

In `Agent/Modules/InjectorModule.cs`, add in the `AddAgent` method before the return statement:

```csharp
services.AddSingleton<IDomainToolRegistry, DomainToolRegistry>();
```

And update the `MultiAgentFactory` creation to include the registry:

```csharp
.AddSingleton<IAgentFactory>(sp =>
    new MultiAgentFactory(
        sp,
        sp.GetRequiredService<IOptionsMonitor<AgentRegistryOptions>>(),
        llmConfig,
        sp.GetRequiredService<IDomainToolRegistry>()))
```

**Step 3: Verify build**

Run: `dotnet build Agent/Agent.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add Agent/Modules/SchedulingModule.cs Agent/Modules/InjectorModule.cs
git commit -m "feat: register domain tool DI services"
```

---

### Task 13: Update Test Fixtures

**Files:**
- Modify: `Tests/Integration/Fixtures/WebChatServerFixture.cs`

**Step 1: Check and update test fixture**

The test fixture creates a fake `IAgentFactory`. It may need to be updated if tests break. Check if `MultiAgentFactory` signature change causes issues.

Run: `dotnet build Tests/Tests.csproj`

If build fails due to `MultiAgentFactory` constructor change, update the fixture to provide a mock `IDomainToolRegistry` or adjust accordingly.

**Step 2: Run tests**

Run: `dotnet test Tests/Tests.csproj`
Expected: All tests pass

**Step 3: Commit if changes needed**

```bash
git add Tests/
git commit -m "test: update fixtures for domain tool registry"
```

---

### Task 14: Integration Test - End to End

**Step 1: Create test configuration**

Create a test `appsettings.json` entry or use existing test setup with:

```json
{
  "agents": [
    {
      "id": "test-agent",
      "name": "Test Agent",
      "model": "google/gemini-2.0-flash-001",
      "mcpServerEndpoints": [],
      "enabledFeatures": ["scheduling"],
      "whitelistPatterns": ["domain:scheduling:*"]
    }
  ]
}
```

**Step 2: Verify agent receives scheduling tools**

Run the application and verify via logs or debugging that:
1. Agent is created with `enabledFeatures: ["scheduling"]`
2. Domain tools are included in the agent's tool list
3. Tool names follow pattern `domain:scheduling:schedule_*`

**Step 3: Final commit**

```bash
git add -A
git commit -m "feat: complete domain tool features implementation"
```

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Add AI package to Domain | `Domain/Domain.csproj` |
| 2 | Add EnabledFeatures property | `Domain/DTOs/AgentDefinition.cs` |
| 3 | Create IDomainToolFeature | `Domain/Contracts/IDomainToolFeature.cs` |
| 4 | Create IDomainToolRegistry | `Domain/Contracts/IDomainToolRegistry.cs` |
| 5 | Update ScheduleCreateTool | `Domain/Tools/Scheduling/ScheduleCreateTool.cs` |
| 6 | Update ScheduleListTool | `Domain/Tools/Scheduling/ScheduleListTool.cs` |
| 7 | Update ScheduleDeleteTool | `Domain/Tools/Scheduling/ScheduleDeleteTool.cs` |
| 8 | Create SchedulingToolFeature | `Domain/Tools/Scheduling/SchedulingToolFeature.cs` |
| 9 | Create DomainToolRegistry | `Infrastructure/Agents/DomainToolRegistry.cs` |
| 10 | Integrate into McpAgent | `Infrastructure/Agents/McpAgent.cs`, `ThreadSession.cs` |
| 11 | Update MultiAgentFactory | `Infrastructure/Agents/MultiAgentFactory.cs` |
| 12 | Register DI services | `Agent/Modules/SchedulingModule.cs`, `InjectorModule.cs` |
| 13 | Update test fixtures | `Tests/Integration/Fixtures/WebChatServerFixture.cs` |
| 14 | Integration test | Verify end-to-end |
