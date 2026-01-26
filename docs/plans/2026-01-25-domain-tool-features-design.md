# Domain Tool Features Design

## Overview

Enable domain scheduling tools to be callable by agents during conversations through AIFunction wrappers, with a configuration-based opt-in mechanism in `AgentDefinition`.

## Key Decisions

1. **Exposure model**: Domain tools as AIFunctions injected at agent creation (no new MCP server)
2. **Configuration**: Feature flags array `EnabledFeatures` in `AgentDefinition`
3. **Tool location**: Domain layer, using `AIFunctionFactory` with `[Description]` attributes
4. **Discovery**: Feature provider pattern via `IDomainToolFeature`
5. **Naming**: Prefixed as `domain:{feature}:{tool_name}`
6. **Access control**: Explicit whitelist patterns required (e.g., `domain:scheduling:*`)

## AgentDefinition Changes

**File:** `Domain/DTOs/AgentDefinition.cs`

```csharp
public record AgentDefinition
{
    // ... existing properties ...

    public string[] EnabledFeatures { get; init; } = [];
}
```

**Configuration example:**

```json
{
  "agents": [
    {
      "id": "jack",
      "name": "Jack",
      "enabledFeatures": ["scheduling"],
      "whitelistPatterns": [
        "mcp:mcp-library:*",
        "domain:scheduling:*"
      ]
    }
  ]
}
```

Both are required:
- `enabledFeatures` controls which domain tools are **available**
- `whitelistPatterns` controls which tools are **allowed** to execute

## Contracts

**File:** `Domain/Contracts/IDomainToolFeature.cs`

```csharp
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolFeature
{
    string FeatureName { get; }
    IEnumerable<AIFunction> GetTools();
}
```

**File:** `Domain/Contracts/IDomainToolRegistry.cs`

```csharp
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolRegistry
{
    IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures);
}
```

## Scheduling Tools with Annotations

**Package addition:** Add `Microsoft.Extensions.AI.Abstractions` to `Domain/Domain.csproj`

**File:** `Domain/Tools/Scheduling/ScheduleCreateTool.cs`

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;

namespace Domain.Tools.Scheduling;

public class ScheduleCreateTool(
    IScheduleStore store,
    ICronValidator cronValidator,
    IAgentDefinitionProvider agentProvider)
{
    public const string Name = "schedule_create";
    public const string Description = "Creates a scheduled agent task that runs at a specified time or recurring interval.";

    [Description(Description)]
    public async Task<JsonNode> RunAsync(
        [Description("Agent ID to execute the task")] string agentId,
        [Description("The prompt/task to execute")] string prompt,
        [Description("Cron expression for recurring schedules")] string? cronExpression = null,
        [Description("ISO 8601 datetime for one-time execution")] DateTime? runAt = null,
        [Description("Channel to send results (telegram/webchat)")] string channel = "telegram",
        [Description("Chat ID for the target conversation")] long? chatId = null,
        CancellationToken ct = default)
    {
        // Existing validation and logic
    }
}
```

Same pattern for `ScheduleListTool` and `ScheduleDeleteTool`.

## Scheduling Feature Provider

**File:** `Domain/Tools/Scheduling/SchedulingToolFeature.cs`

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

## Domain Tool Registry

**File:** `Infrastructure/Agents/DomainToolRegistry.cs`

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

## Agent Factory Integration

**File:** `Infrastructure/Agents/MultiAgentFactory.cs` (modified)

```csharp
public class MultiAgentFactory(
    // ... existing dependencies ...
    IDomainToolRegistry domainToolRegistry) : IAgentFactory, IScheduleAgentFactory
{
    public async Task<IAgent> CreateFromDefinition(
        AgentKey key,
        string? userId,
        AgentDefinition definition,
        CancellationToken ct = default)
    {
        // ... existing MCP client setup ...

        // Get domain tools based on enabled features
        var domainTools = domainToolRegistry
            .GetToolsForFeatures(definition.EnabledFeatures)
            .ToArray();

        // Combine MCP tools with domain tools
        var allTools = mcpTools.Concat(domainTools).ToArray();

        // Create agent with combined tools
        // ... rest of agent creation ...
    }
}
```

## File Changes Summary

**New files:**
- `Domain/Contracts/IDomainToolFeature.cs`
- `Domain/Contracts/IDomainToolRegistry.cs`
- `Domain/Tools/Scheduling/SchedulingToolFeature.cs`
- `Infrastructure/Agents/DomainToolRegistry.cs`

**Modified files:**
- `Domain/Domain.csproj` - Add `Microsoft.Extensions.AI.Abstractions`
- `Domain/DTOs/AgentDefinition.cs` - Add `EnabledFeatures` property
- `Domain/Tools/Scheduling/ScheduleCreateTool.cs` - Add `[Description]` attributes
- `Domain/Tools/Scheduling/ScheduleListTool.cs` - Add `[Description]` attributes
- `Domain/Tools/Scheduling/ScheduleDeleteTool.cs` - Add `[Description]` attributes
- `Infrastructure/Agents/MultiAgentFactory.cs` - Inject registry, combine tools
- `Agent/Modules/SchedulingModule.cs` - Register `SchedulingToolFeature`
- `Agent/Modules/AgentModule.cs` - Register `DomainToolRegistry`

**No changes needed:**
- Tool approval handler (already uses whitelist patterns)
- Existing MCP tool flow (unchanged)
