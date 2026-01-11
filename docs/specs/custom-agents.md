# Custom Agents from Data - Feature Specification

## Overview

Enable a single container instance to run multiple agents defined as data, each with unique MCP server configurations, custom instructions, and optional LLM overrides.

## Agent Definition Schema

### Domain/DTOs/AgentDefinition.cs

```csharp
public record AgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string[] WhitelistPatterns { get; init; } = [];
    public string? CustomInstructions { get; init; }
    public LlmConfiguration? LlmOverrides { get; init; }
}

public record LlmConfiguration
{
    public string? Model { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? ReasoningEffort { get; init; }
}
```

### Domain/DTOs/AgentBinding.cs

```csharp
public record AgentBinding
{
    public required string AgentId { get; init; }
    public required AgentBindingType Type { get; init; }
    public required string BindingKey { get; init; }
}

public enum AgentBindingType
{
    TelegramBot,
    ChatId,
    UserId
}
```

## Storage Architecture

### IAgentRegistry Interface (Domain/Contracts)

```csharp
public interface IAgentRegistry
{
    Task<AgentDefinition?> GetAgentAsync(string agentId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentDefinition>> GetAllAgentsAsync(CancellationToken ct = default);
    Task<AgentDefinition> CreateAgentAsync(AgentDefinition agent, CancellationToken ct = default);
    Task<AgentDefinition> UpdateAgentAsync(AgentDefinition agent, CancellationToken ct = default);
    Task<bool> DeleteAgentAsync(string agentId, CancellationToken ct = default);
}
```

### Implementations (Infrastructure/Agents/Registry)

| Class | Purpose |
|-------|---------|
| `FileAgentRegistry` | Read-only, loads from config/JSON, hot-reloadable |
| `RedisAgentRegistry` | Read-write, runtime agent creation, key: `agent:definition:{id}` |
| `CompositeAgentRegistry` | Combines both, file takes precedence |

## Agent Selection

### Bot-Per-Agent Model

Each Telegram bot token maps to exactly one agent. This is the simplest and clearest approach:
- One bot = one agent identity
- No confusion for users about which agent they're talking to
- Clear separation of concerns

### IAgentBindingResolver Interface (Domain/Contracts)

```csharp
public interface IAgentBindingResolver
{
    Task<string?> ResolveAgentIdAsync(string telegramBotTokenHash, CancellationToken ct = default);
}
```

### Resolution Priority

1. **Telegram bot binding** - Bot token maps to agent (primary mechanism)
2. **Default agent** - Config fallback when no binding exists

## Factory Changes

### MultiAgentFactory (Infrastructure/Agents)

Replaces `McpAgentFactory` when multi-agent mode is detected:

```csharp
public sealed class MultiAgentFactory(
    IServiceProvider serviceProvider,
    IAgentRegistry agentRegistry,
    IAgentBindingResolver bindingResolver,
    LlmConfiguration defaultLlmConfig) : IAgentFactory
{
    public DisposableAgent Create(AgentKey agentKey, string userId)
    {
        // 1. Resolve agent ID via binding resolver
        // 2. Load agent definition from registry
        // 3. Create chat client with LLM overrides
        // 4. Create McpAgent with custom instructions
    }
}
```

### McpAgent Changes

Add `customInstructions` parameter to constructor. Update `CreateRunOptions`:

```csharp
var prompts = session.ClientManager.Prompts
    .Prepend(BasePrompt.Instructions);

if (!string.IsNullOrEmpty(customInstructions))
    prompts = prompts.Prepend(customInstructions);

prompts = prompts.Prepend($"Current time: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
```

Order: Time -> Custom Instructions -> Base Prompt -> MCP Prompts

## Configuration

### Multi-Agent Config (agents.json or env vars)

```json
{
  "agents": [
    {
      "id": "jack",
      "name": "Jack",
      "mcpServerEndpoints": ["http://mcp-library:8080/sse", "http://mcp-websearch:8080/sse"],
      "whitelistPatterns": ["mcp:mcp-library:*", "mcp:mcp-websearch:*"],
      "customInstructions": "You are Jack, a media library assistant..."
    }
  ],
  "bindings": [
    { "agentId": "jack", "type": "TelegramBot", "bindingKey": "BOT_TOKEN_HASH" }
  ],
  "defaultAgentId": "jack"
}
```

### Environment Variables

```bash
AGENTS__0__ID=jack
AGENTS__0__NAME=Jack
AGENTS__0__MCPSERVERENDPOINTS__0=http://mcp-library:8080/sse
AGENTS__0__CUSTOMINSTRUCTIONS=You are Jack...
DEFAULTAGENTID=jack
```

## Backward Compatibility

Single-agent mode detected when:
- `NAME` env var is set (current pattern)
- `Agents` config array is empty

Legacy settings auto-convert to `AgentDefinition`:
```csharp
public static AgentDefinition GetLegacyAgentDefinition(this AgentSettings settings)
{
    return new AgentDefinition
    {
        Id = settings.Name.ToLowerInvariant(),
        Name = settings.Name,
        McpServerEndpoints = settings.McpServers.Select(m => m.Endpoint).ToArray(),
        WhitelistPatterns = settings.WhitelistPatterns
    };
}
```

## Implementation Phases

### Phase 1: Foundation
1. Create `AgentDefinition`, `AgentBinding`, `LlmConfiguration` DTOs
2. Create `IAgentRegistry`, `IAgentBindingResolver` interfaces
3. Implement `FileAgentRegistry`, `RedisAgentRegistry`, `CompositeAgentRegistry`
4. Implement `AgentBindingResolver`

### Phase 2: Agent Creation
1. Add `customInstructions` parameter to `McpAgent`
2. Update prompt composition in `CreateRunOptions`
3. Create `MultiAgentFactory`
4. Add mode detection in `ConfigModule`

### Phase 3: Integration
1. Update `InjectorModule` for conditional factory registration
2. Add mode detection logic in `ConfigModule`

### Phase 4: Multi-Bot Support
1. Support multiple Telegram bot tokens in config
2. Each bot token binds to one agent via `bindings` config
3. `MultiTelegramBotClient` - polls all configured bots
4. Include bot token hash in `ChatPrompt` for agent resolution

## Critical Files

| File | Changes |
|------|---------|
| `Domain/DTOs/AgentDefinition.cs` | New - agent definition record |
| `Domain/DTOs/AgentBinding.cs` | New - binding types and record |
| `Domain/Contracts/IAgentRegistry.cs` | New - registry interface |
| `Domain/Contracts/IAgentBindingResolver.cs` | New - binding resolution |
| `Infrastructure/Agents/Registry/*.cs` | New - registry implementations |
| `Infrastructure/Agents/McpAgent.cs` | Add customInstructions parameter |
| `Infrastructure/Agents/MultiAgentFactory.cs` | New - multi-agent factory |
| `Infrastructure/Clients/MultiTelegramBotClient.cs` | New - polls multiple bots |
| `Agent/Modules/InjectorModule.cs` | Conditional factory registration |
| `Agent/Settings/AgentSettings.cs` | Add multi-agent config support |

## Verification

1. **Unit tests**: Registry CRUD, bot-to-agent binding resolution
2. **Integration test**: Create agent via Redis, verify it's usable
3. **Manual test**:
   - Start with legacy single-agent config (NAME env var), verify backward compat
   - Switch to multi-agent config with two bots, verify each bot uses correct agent
   - Test LLM override (different model per agent)
