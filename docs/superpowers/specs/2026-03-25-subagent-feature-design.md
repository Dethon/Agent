# Subagent Feature Design

## Overview

Enable the parent agent to spawn a child agent instance with a different system prompt, fresh context, and its own tool set to handle a subtask synchronously and return a result. This keeps context smaller for complex tasks by delegating work to purpose-built subagents.

## Requirements

- Subagent profiles are predefined in `appsettings.json` (model, MCP endpoints, system prompt)
- Parent agent triggers subagents via a domain tool (like scheduling)
- Subagents run synchronously — the parent blocks until the result is returned
- One level deep only — subagents cannot spawn subagents (enforced at startup)
- Subagent conversation history is ephemeral (no Redis persistence)
- Tool approval uses the parent's `IToolApprovalHandler` and whitelist patterns
- Subagent profiles do not define their own whitelist patterns
- Resource subscriptions (channel magic) are disabled for subagents
- Configurable execution timeout per subagent profile
- Subagent token usage and tool calls are attributed to the subagent's own profile ID in metrics

## Configuration

Subagent profiles live in `appsettings.json` under a new `subAgents` array:

```json
{
  "agents": [ ... ],
  "subAgents": [
    {
      "id": "summarizer",
      "name": "Summarizer",
      "description": "Summarizes long content into concise bullet points",
      "model": "google/gemini-2.5-flash",
      "mcpServerEndpoints": [],
      "customInstructions": "You are a summarization specialist. Return concise bullet points.",
      "enabledFeatures": [],
      "maxExecutionSeconds": 120
    },
    {
      "id": "researcher",
      "name": "Researcher",
      "description": "Performs web research and returns findings",
      "model": "google/gemini-2.5-flash",
      "mcpServerEndpoints": ["http://mcp-websearch:5010/sse"],
      "customInstructions": "You are a research assistant. Search the web and return structured findings.",
      "enabledFeatures": [],
      "maxExecutionSeconds": 300
    }
  ]
}
```

Parent agents activate the subagent feature by including `"subagents"` in their `enabledFeatures` array.

### Recursion Prevention

Enforced at startup in `SubAgentModule.AddSubAgents()`: validate that no `SubAgentDefinition` includes `"subagents"` in its `enabledFeatures`. Throw `InvalidOperationException` if violated. This fails fast and avoids runtime surprises.

### SubAgentDefinition

A new record similar to `AgentDefinition` but without `whitelistPatterns` and `telegramBotToken`:

```csharp
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

## Domain Tool

A single tool `domain:subagents:run_subagent` following the scheduling domain tool pattern.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `subAgentId` | `string` | Yes | ID of the subagent profile to use |
| `prompt` | `string` | Yes | The task/prompt to send to the subagent |

### Tool Description

The tool's `[Description]` attribute is dynamically generated to include the list of available subagent profiles with their IDs and descriptions, so the LLM knows which subagent to pick. Example:

```
Runs a task on a subagent with a fresh context and returns the result.
Available subagents:
- "summarizer": Summarizes long content into concise bullet points
- "researcher": Performs web research and returns findings
```

### Return Value

`JsonNode` with:
- `status`: `"completed"` or `"error"`
- `result`: The subagent's final text response (on success)
- `error`: Error message (on failure)

### Execution Flow

1. Look up the subagent profile from `SubAgentRegistryOptions` by `subAgentId`
2. Validate the profile exists; return error if not found
3. Call `ISubAgentRunner.RunAsync()` with the profile, prompt, parent context, and cancellation token
4. Return the response text as the tool result

## ISubAgentRunner — Domain/Infrastructure Bridge

The `SubAgentRunTool` lives in `Domain/Tools/` and cannot reference Infrastructure types. A new interface `ISubAgentRunner` in `Domain/Contracts/` bridges this:

```csharp
public interface ISubAgentRunner
{
    Task<string> RunAsync(
        SubAgentDefinition definition,
        string prompt,
        SubAgentContext parentContext,
        CancellationToken ct = default);
}
```

### SubAgentContext

A new record in `Domain/DTOs/` that carries the parent's contextual information needed for subagent creation:

```csharp
public record SubAgentContext(
    IToolApprovalHandler ApprovalHandler,
    string[] WhitelistPatterns,
    string UserId);
```

### How the Tool Gets Parent Context

Domain tools are created at agent creation time via `DomainToolRegistry.GetToolsForFeatures()` and receive dependencies through DI. However, `IToolApprovalHandler`, whitelist patterns, and `userId` are per-agent contextual values not available in DI.

Solution: `SubAgentRunTool` accepts an `ISubAgentContextAccessor` (scoped/ambient context pattern):

```csharp
public interface ISubAgentContextAccessor
{
    SubAgentContext? Context { get; set; }
}
```

- Registered as **singleton** in DI (stateless holder)
- `MultiAgentFactory.CreateFromDefinition()` sets the context on the accessor when creating an agent that has `"subagents"` enabled
- `SubAgentRunTool.RunAsync()` reads from the accessor

Since the parent agent already has the approval handler, whitelist, and userId at creation time, the factory sets these on the accessor. The accessor holds a `ConcurrentDictionary<string, SubAgentContext>` keyed by agent name to support multiple concurrent parent agents.

Refined interface:

```csharp
public interface ISubAgentContextAccessor
{
    void SetContext(string agentName, SubAgentContext context);
    SubAgentContext? GetContext(string agentName);
    void RemoveContext(string agentName);
}
```

The `SubAgentRunTool` also needs the parent agent's name to look up its context. This is passed as a closure-captured value when the `AIFunction` is created in `SubAgentToolFeature`.

## SubAgentRunner Implementation

`SubAgentRunner` in `Infrastructure/Agents/` implements `ISubAgentRunner`:

1. Creates an `OpenRouterChatClient` for the subagent's model
2. Wraps it in a `ToolApprovalChatClient` using the parent's approval handler and whitelist patterns
3. Creates a `NullThreadStateStore`
4. Resolves domain tools from the subagent's `enabledFeatures` (excluding `"subagents"`)
5. Instantiates `McpAgent` with resource subscriptions disabled
6. Creates a linked `CancellationTokenSource` with `TimeSpan.FromSeconds(definition.MaxExecutionSeconds)` timeout
7. Creates a session and runs a single user message through it (non-streaming `RunAsync`)
8. Disposes the agent
9. Returns the response text

## Disabling Resource Subscriptions

`ThreadSessionBuilder` gains a flag to skip creating `McpResourceManager`. When disabled:

- `ThreadSessionBuilder.BuildAsync()` skips `CreateResourceManagerAsync()` entirely
- `ThreadSessionData.ResourceManager` becomes nullable (`McpResourceManager?`)
- Methods requiring null-guarding:
  - `McpAgent.RunCoreStreamingAsync()` — skip merge/drain when null
  - `McpAgent.RunStreamingCoreAsync()` — skip `SyncResourcesAsync` call when null
  - `McpAgent.DisposeAsync()` — skip disposing when null
  - `ThreadSession.DisposeAsync()` — skip disposing when null
- `ThreadSession.CreateAsync()` accepts a boolean parameter (`enableResourceSubscriptions`, default `true`)

The `McpAgent` constructor gains a matching `enableResourceSubscriptions` parameter (default `true`) that is forwarded to `ThreadSession.CreateAsync()`.

## Ephemeral State Store

A `NullThreadStateStore` implementation of `IThreadStateStore`:

- `GetMessagesAsync()` → returns `null`
- `SetMessagesAsync()` → no-op
- `DeleteAsync()` → no-op
- `ExistsAsync()` → returns `false`
- `GetAllTopicsAsync()` → returns empty list
- `SaveTopicAsync()` → no-op
- `DeleteTopicAsync()` → no-op
- `GetTopicByChatIdAndThreadIdAsync()` → returns `null`

This prevents subagent conversations from polluting Redis.

## Tool Approval

The subagent receives the parent's `IToolApprovalHandler` instance and whitelist patterns. This means:
- The `ToolApprovalChatClient` wrapping the subagent's LLM client uses the parent's whitelist patterns
- Tool calls matching the parent's whitelist auto-execute; others go through the parent's approval handler
- The user sees approval prompts as if the parent agent called the tool

## Metrics

Subagent token usage and tool calls are tracked under the subagent's profile `id` (e.g., `"summarizer"`, `"researcher"`). `SubAgentRunner` creates a dedicated `AgentMetricsPublisher` using `definition.Id`, so subagent costs appear as separate entries in the dashboard. This provides visibility into subagent usage without conflating it with the parent.

## Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `SubAgentDefinition` | `Domain/DTOs/SubAgentDefinition.cs` | Profile record |
| `SubAgentRegistryOptions` | `Domain/DTOs/SubAgentRegistryOptions.cs` | Options class holding `SubAgentDefinition[]` |
| `SubAgentContext` | `Domain/DTOs/SubAgentContext.cs` | Parent context record (approval handler, whitelist, userId) |
| `ISubAgentRunner` | `Domain/Contracts/ISubAgentRunner.cs` | Interface for running subagents |
| `ISubAgentContextAccessor` | `Domain/Contracts/ISubAgentContextAccessor.cs` | Ambient context for parent agent info |
| `SubAgentRunTool` | `Domain/Tools/SubAgents/SubAgentRunTool.cs` | Domain tool implementation |
| `SubAgentToolFeature` | `Domain/Tools/SubAgents/SubAgentToolFeature.cs` | `IDomainToolFeature` registration (feature name: `"subagents"`) |
| `SubAgentRunner` | `Infrastructure/Agents/SubAgentRunner.cs` | `ISubAgentRunner` implementation |
| `SubAgentContextAccessor` | `Infrastructure/Agents/SubAgentContextAccessor.cs` | `ISubAgentContextAccessor` implementation |
| `NullThreadStateStore` | `Infrastructure/StateManagers/NullThreadStateStore.cs` | No-op state store |
| `SubAgentModule` | `Agent/Modules/SubAgentModule.cs` | DI registration + startup validation |
| `ThreadSessionBuilder` | `Infrastructure/Agents/ThreadSession.cs` | Modified — skip resource manager when flag is off |
| `ThreadSessionData` | `Infrastructure/Agents/ThreadSession.cs` | Modified — nullable `ResourceManager` |
| `McpAgent` | `Infrastructure/Agents/McpAgent.cs` | Modified — handle nullable `ResourceManager`, new constructor param |
| `MultiAgentFactory` | `Infrastructure/Agents/MultiAgentFactory.cs` | Modified — set `SubAgentContextAccessor` on agent creation |

## DI Registration

`SubAgentModule.AddSubAgents()`:
- Binds `SubAgentRegistryOptions` from config section `"subAgents"`
- Validates no subagent profile has `"subagents"` in `enabledFeatures` (fail-fast)
- Registers `ISubAgentRunner` → `SubAgentRunner` as transient
- Registers `ISubAgentContextAccessor` → `SubAgentContextAccessor` as singleton
- Registers `SubAgentRunTool` as transient
- Registers `SubAgentToolFeature` as `IDomainToolFeature`

Wired in `ConfigModule.ConfigureAgents()` alongside `AddScheduling()`.

## Docker Compose

No new environment variables needed — subagent config lives in `appsettings.json` and uses existing `OPENROUTER_*` variables.

## Error Handling

- Unknown `subAgentId` → return `{ "status": "error", "error": "Unknown subagent: {id}" }`
- Subagent execution failure → catch, dispose agent, return `{ "status": "error", "error": "{message}" }`
- Timeout → `OperationCanceledException` from linked CTS → return `{ "status": "error", "error": "Subagent execution timed out after {n} seconds" }`
- Cancellation from parent → propagate `CancellationToken`; subagent respects it

## Testing

### Unit Tests

Lightweight tests for pure logic that doesn't need infrastructure:

- `SubAgentRunTool`: mock `ISubAgentRunner` + `ISubAgentContextAccessor` + options — verify profile lookup, unknown profile error, missing context error
- `SubAgentToolFeature`: verify tool registration under `"subagents"` feature name
- Startup validation: verify `InvalidOperationException` when subagent has `"subagents"` in features

### Integration Tests

Follow the existing `McpAgentIntegrationTests` pattern: real Redis (via `RedisFixture`/Testcontainers), real MCP server (via fixture), real OpenRouter LLM calls, `SkippableFact` for API key gating.

**`SubAgentIntegrationTests`** — tests the full subagent invocation end-to-end:

- **Fixture**: `RedisFixture` (for parent agent state) + a lightweight MCP server fixture (if subagent profiles need tools)
- **Real LLM**: Uses `OpenRouterChatClient` with user secrets API key, `SkippableFact` if key is absent
- **Tests**:
  - Parent agent calls `run_subagent` tool and receives the subagent's response as a tool result
  - Subagent with MCP tools can use them and return results
  - Subagent conversation is ephemeral (no Redis keys created for the subagent)
  - Timeout enforcement — subagent with a very short `MaxExecutionSeconds` returns a timeout error
  - Parent agent continues normally after subagent completes (context isolation verified)

**`NullThreadStateStoreTests`** — simple integration-style test verifying all methods return expected no-op values (no infrastructure needed, but tests the full interface contract)

**`ThreadSessionBuilder` resource subscription flag** — test that `enableResourceSubscriptions: false` produces a `ThreadSessionData` with null `ResourceManager` (uses a real MCP server fixture to verify the builder actually skips subscription setup)
