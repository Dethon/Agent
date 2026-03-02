# Custom Agent Registration via SignalR

## Goal

Allow end users to register personalized agents at runtime through SignalR hub methods. Custom agents are session-only (in-memory), visible only to the creator, and support full customization of model, MCP servers, instructions, and features.

## Scope

Backend only. No WebChat.Client changes.

## Changes

### 1. New DTO: `CustomAgentRegistration`

**File:** `Domain/DTOs/WebChat/CustomAgentRegistration.cs`

```csharp
public record CustomAgentRegistration
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string[] WhitelistPatterns { get; init; } = [];
    public string? CustomInstructions { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
}
```

### 2. `IAgentFactory` Interface Changes

**File:** `Domain/Contracts/IAgentFactory.cs`

Add two methods:

```csharp
AgentInfo RegisterCustomAgent(string userId, CustomAgentRegistration registration);
bool UnregisterCustomAgent(string userId, string agentId);
```

Update existing method signature:

```csharp
IReadOnlyList<AgentInfo> GetAvailableAgents(string? userId = null);
```

When `userId` is null, returns only built-in agents. When provided, merges built-in agents with the user's custom agents.

### 3. `MultiAgentFactory` Implementation

**File:** `Infrastructure/Agents/MultiAgentFactory.cs`

Add an in-memory store:

```csharp
private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AgentDefinition>> _customAgents = new();
```

Keyed by userId, then by agentId.

- **`RegisterCustomAgent`**: Generate ID as `custom-{guid}`, create `AgentDefinition` from the registration DTO, store under the user's dictionary, return `AgentInfo`.
- **`UnregisterCustomAgent`**: Remove the agent from the user's dictionary. Return false if not found.
- **`GetAvailableAgents(userId)`**: Return built-in agents from `AgentRegistryOptions`. If userId is provided, append the user's custom agents.
- **`Create()`**: When resolving an agent definition by ID, check built-in config first, then fall back to the user's custom agents dictionary.

### 4. `ChatHub` Hub Methods

**File:** `Agent/Hubs/ChatHub.cs`

```csharp
public AgentInfo RegisterCustomAgent(CustomAgentRegistration registration)
{
    // Extract userId from connection state (set by RegisterUser)
    // Validate Name and Model are non-empty; throw HubException if invalid
    // Delegate to IAgentFactory.RegisterCustomAgent(userId, registration)
    // Return the new AgentInfo
}

public bool UnregisterCustomAgent(string agentId)
{
    // Extract userId from connection state
    // Delegate to IAgentFactory.UnregisterCustomAgent(userId, agentId)
}
```

Update the existing `GetAgents` method to pass the current userId:

```csharp
public IReadOnlyList<AgentInfo> GetAgents()
{
    var userId = GetCurrentUserId();
    return _agentFactory.GetAvailableAgents(userId);
}
```

## Validation

- `Name` and `Model` must be non-empty strings. Throw `HubException` on violation.
- No limit on number of custom agents per user.

## Out of Scope

- Persistence across server restarts.
- Visibility to other users.
- WebChat.Client UI for creating/managing custom agents.
- Server-side discovery of available models or MCP servers.

## Files Modified

| File | Change |
|------|--------|
| `Domain/DTOs/WebChat/CustomAgentRegistration.cs` | New file |
| `Domain/Contracts/IAgentFactory.cs` | Add Register/Unregister methods, update GetAvailableAgents signature |
| `Infrastructure/Agents/MultiAgentFactory.cs` | In-memory per-user storage, merged lookups |
| `Agent/Hubs/ChatHub.cs` | New hub methods, pass userId to GetAgents |
