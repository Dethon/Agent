# Domain Feature Prompts: Proactive Subagent Usage

**Date:** 2026-03-27
**Goal:** Make agents with subagents enabled more proactive about delegating work to subagents.

## Problem

Agents with the `subagents` feature enabled see the `run_subagent` tool but receive no system prompt guidance on when to use it. Other features (Memory, WebBrowsing, etc.) include detailed prompts served via their MCP servers, but subagents are a domain tool feature with no prompt mechanism. The result is that agents rarely use subagents unless the user explicitly asks.

## Solution

Extend `IDomainToolFeature` so domain features can contribute system prompt segments, then add a `SubAgentPrompt` with guidance on proactive delegation.

## Architecture

### Current prompt assembly

```
MCP server prompts + BasePrompt.Instructions + CustomInstructions
         ↓
   McpAgent.CreateRunOptions() → ChatOptions.Instructions
```

### New prompt assembly

```
MCP server prompts + Domain feature prompts + BasePrompt.Instructions + CustomInstructions
         ↓
   McpAgent.CreateRunOptions() → ChatOptions.Instructions
```

Domain feature prompts are collected from enabled `IDomainToolFeature` instances via a new `Prompt` property and a new `GetPromptsForFeatures()` method on the registry.

## Changes

### 1. `IDomainToolFeature` — add Prompt property

```csharp
public interface IDomainToolFeature
{
    string FeatureName { get; }
    string? Prompt => null;  // Default interface implementation
    IEnumerable<AIFunction> GetTools(FeatureConfig config);
}
```

Default `null` means existing features (scheduling, chat monitoring) don't need changes.

### 2. `IDomainToolRegistry` / `DomainToolRegistry` — add prompt collection

```csharp
public interface IDomainToolRegistry
{
    IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config);
    IEnumerable<string> GetPromptsForFeatures(IEnumerable<string> enabledFeatures);
}
```

Implementation filters enabled features, selects non-null `Prompt` values.

### 3. `SubAgentPrompt` (new file: `Domain/Prompts/SubAgentPrompt.cs`)

Static class following the same pattern as `MemoryPrompt`, `WebBrowsingPrompt`, etc. Contains guidance for:

- **Parallel decomposition**: When a request contains multiple independent parts, spawn subagents to handle them concurrently rather than doing them sequentially.
- **Heavy task delegation**: Delegate research, web searches, complex data gathering, or multi-step operations to subagents so the main agent stays responsive.
- **Self-contained prompts**: Always provide subagents with complete, self-contained instructions since they have a fresh context with no conversation history.
- **Result synthesis**: After subagents complete, synthesize their results into a coherent response.

The prompt should list available subagents and their capabilities (dynamically from the registry), but the static text provides the behavioral guidance.

### 4. `SubAgentToolFeature` — return the prompt

```csharp
public class SubAgentToolFeature(SubAgentRegistryOptions registryOptions) : IDomainToolFeature
{
    public string FeatureName => "subagents";
    public string? Prompt => SubAgentPrompt.SystemPrompt;
    // ... existing GetTools implementation
}
```

### 5. `McpAgent` — accept and include domain prompts

Add an `IReadOnlyList<string>? domainPrompts` parameter. In `CreateRunOptions`, include them alongside MCP server prompts:

```csharp
private ChatClientAgentRunOptions CreateRunOptions(ThreadSession session)
{
    var prompts = session.ClientManager.Prompts
        .Concat(_domainPrompts)
        .Prepend(BasePrompt.Instructions);
    // ... rest unchanged
}
```

### 6. `MultiAgentFactory` — pass domain prompts

When constructing `McpAgent`, call `domainToolRegistry.GetPromptsForFeatures(enabledFeatures)` and pass the result.

## SubAgentPrompt Content

```
## Subagent Delegation

You have access to subagents — lightweight workers that run tasks independently with their own
context. Use them proactively to improve response quality and speed.

### When to Delegate

- **Parallel tasks**: When a request involves multiple independent parts (e.g., "search for X
  and also look up Y"), spawn subagents for each part concurrently instead of doing them
  sequentially.
- **Heavy operations**: Delegate research, web searches, multi-step data gathering, or any
  task requiring many tool calls. This keeps you responsive and lets the subagent focus on
  the work.
- **Exploration**: When you need to investigate multiple options or approaches, send subagents
  to explore different paths simultaneously.

### When NOT to Delegate

- Simple, single-tool-call tasks (faster to do yourself)
- Tasks that require conversation context the subagent won't have
- Follow-up questions or clarifications with the user

### How to Delegate Effectively

- **Self-contained prompts**: Subagents have NO conversation history. Include ALL necessary
  context, URLs, names, and requirements in the prompt.
- **Clear success criteria**: Tell the subagent what a good result looks like.
- **Synthesize results**: After subagents complete, combine their outputs into a coherent
  response for the user. Don't just relay raw results.
```

## Files Changed

| File | Change |
|------|--------|
| `Domain/Contracts/IDomainToolFeature.cs` | Add `string? Prompt` with default `null` |
| `Domain/Contracts/IDomainToolRegistry.cs` | Add `GetPromptsForFeatures()` method |
| `Domain/Prompts/SubAgentPrompt.cs` | New file with delegation guidance |
| `Domain/Tools/SubAgents/SubAgentToolFeature.cs` | Return `SubAgentPrompt.SystemPrompt` |
| `Infrastructure/Agents/DomainToolRegistry.cs` | Implement `GetPromptsForFeatures()` |
| `Infrastructure/Agents/McpAgent.cs` | Accept and include domain prompts |
| `Infrastructure/Agents/MultiAgentFactory.cs` | Pass domain prompts to `McpAgent` |
| `Tests/` | Unit tests for new prompt assembly and registry methods |

## Testing

- `DomainToolRegistryTests`: Verify `GetPromptsForFeatures` collects non-null prompts from enabled features and ignores disabled/null ones.
- `SubAgentToolFeatureTests`: Verify `Prompt` returns non-null content.
- `McpAgentTests` (if applicable): Verify domain prompts are included in assembled instructions.
