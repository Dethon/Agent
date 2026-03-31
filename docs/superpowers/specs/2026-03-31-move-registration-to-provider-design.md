# Move Custom Agent Registration to AgentDefinitionProvider

## Problem

`IAgentFactory` / `MultiAgentFactory` currently owns three responsibilities that belong in the definition provider:

- `RegisterCustomAgent(userId, registration)` — creates and stores custom agent definitions
- `UnregisterCustomAgent(userId, agentId)` — removes custom agent definitions
- `GetAvailableAgents(userId)` — queries built-in + custom agent definitions

These are definition-management concerns, not instance-creation concerns. The factory should only create agents from definitions.

## Approach

Expand `IAgentDefinitionProvider` to include registration methods. The factory becomes a pure instantiation layer that resolves definitions through the provider.

## Interface Changes

### `IAgentDefinitionProvider` (expanded)

```csharp
public interface IAgentDefinitionProvider
{
    AgentDefinition? GetById(string agentId);
    IReadOnlyList<AgentDefinition> GetAll(string? userId = null);
    AgentDefinition RegisterCustomAgent(string userId, CustomAgentRegistration registration);
    bool UnregisterCustomAgent(string userId, string agentId);
}
```

- `GetAll(userId)` merges built-in agents with the user's custom agents (replaces `GetAvailableAgents`).
- `RegisterCustomAgent` returns `AgentDefinition` (not `AgentInfo` — callers map to presentation DTOs).
- `GetById` unchanged (already resolves both built-in and custom).

### `IAgentFactory` (shrunk)

```csharp
public interface IAgentFactory
{
    DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler);
    DisposableAgent CreateSubAgent(SubAgentDefinition definition, IToolApprovalHandler approvalHandler, string[] whitelistPatterns, string userId);
}
```

Removed: `GetAvailableAgents`, `RegisterCustomAgent`, `UnregisterCustomAgent`.

## Implementation Changes

### `AgentDefinitionProvider`

Absorbs registration logic from `MultiAgentFactory`:

- `RegisterCustomAgent`: generates `custom-{Guid}` ID, creates `AgentDefinition` from `CustomAgentRegistration`, stores via `CustomAgentRegistry`, returns the definition.
- `UnregisterCustomAgent`: delegates to `CustomAgentRegistry.Remove`.
- `GetAll(userId)`: merges `registryOptions.CurrentValue.Agents` with `customAgentRegistry.GetByUser(userId)`.

Already has both `IOptionsMonitor<AgentRegistryOptions>` and `CustomAgentRegistry` dependencies.

### `MultiAgentFactory`

- Drops `CustomAgentRegistry` dependency.
- Drops `IOptionsMonitor<AgentRegistryOptions>` dependency.
- Takes `IAgentDefinitionProvider` dependency instead.
- `Create()` resolves definitions via `provider.GetById(agentId)` or `provider.GetAll().First()` for the default agent.
- No longer implements `GetAvailableAgents`, `RegisterCustomAgent`, `UnregisterCustomAgent`.

### `Program.cs` (API endpoints)

- `GET /api/agents` — injects `IAgentDefinitionProvider`, calls `GetAll(userId)`, maps to `AgentInfo`.
- `POST /api/agents` — injects `IAgentDefinitionProvider`, calls `RegisterCustomAgent`, maps result to `AgentInfo`.
- `DELETE /api/agents/{agentId}` — injects `IAgentDefinitionProvider`, calls `UnregisterCustomAgent`.

### Test Changes

- Registration/unregistration/query tests move from `MultiAgentFactoryTests` to a new `AgentDefinitionProviderTests`.
- `FakeAgentFactory` (integration fixture) — remove the three delegated methods.
- `MonitorTests` stub — remove the three stub methods from the anonymous `IAgentFactory` implementation.
- Factory tests that previously registered custom agents before calling `Create()` will set up via the provider or registry directly.

## Data Flow

```
Registration:  Client -> Program.cs -> IAgentDefinitionProvider.RegisterCustomAgent -> CustomAgentRegistry
Query:         Client -> Program.cs -> IAgentDefinitionProvider.GetAll(userId) -> built-in + CustomAgentRegistry
Agent creation: Client -> Program.cs -> IAgentFactory.Create -> IAgentDefinitionProvider.GetById -> CreateFromDefinition
```

## Out of Scope

- Persisting custom agents (currently in-memory only — no change).
- Changing `CustomAgentRegistry` internals.
- Changing `IScheduleAgentFactory` or `CreateFromDefinition` signatures.
