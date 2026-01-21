# Phase 10: Backend Integration - Research

**Researched:** 2026-01-21
**Domain:** SignalR connection identity management, ASP.NET Core Hub patterns, message attribution
**Confidence:** HIGH

## Summary

This phase implements backend awareness of user identity for personalized agent responses. The frontend (Phases 8-9) already captures and displays sender identity locally; this phase wires the server side to register users, track identity per connection, persist sender attribution, and pass username to the agent.

**Key Technical Domains:**
1. **SignalR Connection State:** Using `Context.Items` for per-connection identity storage
2. **Hub Method Design:** Registration pattern with validation guards
3. **Message Attribution:** Extending DTOs and persistence to include sender information
4. **Agent Personalization:** Passing username through the agent factory to prompt context

**Primary recommendation:** Use SignalR's built-in `Context.Items` for per-connection state, add a `RegisterUser` hub method that validates and stores identity, guard other hub methods with identity checks, and extend the message persistence layer to include senderId.

## Standard Stack

The established patterns for this domain:

### Core
| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| SignalR Hub Context | .NET 10 | Per-connection state storage | Built-in mechanism for connection-scoped data |
| Context.Items | .NET 10 | Key/value storage per connection | Recommended by Microsoft for per-connection state |
| Hub.Context.ConnectionId | .NET 10 | Unique connection identifier | Built-in, automatic lifecycle management |

### Supporting
| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| Hub.OnConnectedAsync | .NET 10 | Connection lifecycle hook | For setup requiring async operations |
| Hub.OnDisconnectedAsync | .NET 10 | Cleanup on disconnect | Automatic cleanup (Groups already handled) |

**No additional packages required** - all functionality is built into ASP.NET Core SignalR.

## Architecture Patterns

### Recommended Hub Method Flow

**Registration Pattern:**
```
Client connects → RegisterUser(userId) → Server validates → Store in Context.Items → Allow subsequent calls
```

**Message Flow with Identity:**
```
SendMessage(topicId, message, senderId) → Validate registered → Use stored username → Pass to agent
```

### Pattern 1: Per-Connection Identity Storage
**What:** Store user identity in `Context.Items` after registration, validate on each hub method call
**When to use:** When user identity is app-level (not authenticated via JWT/claims)

**Example:**
```csharp
// Source: https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs
public async Task RegisterUser(string userId)
{
    // Validate userId exists in system
    var user = await _userService.GetUserAsync(userId);
    if (user is null)
    {
        throw new HubException("Invalid user ID");
    }

    // Store in connection-scoped state
    Context.Items["UserId"] = userId;
    Context.Items["Username"] = user.Username;
}

public async Task SendMessage(string topicId, string message, string senderId)
{
    // Validate registration
    if (!Context.Items.TryGetValue("UserId", out var registeredUserId))
    {
        throw new HubException("User not registered. Call RegisterUser first.");
    }

    // Trust senderId from client, but we have registered username for agent
    var username = Context.Items["Username"] as string;
    // ... process message with username
}
```

### Pattern 2: Message Attribution in Persistence
**What:** Extend message DTOs to include senderId, persist to Redis alongside content
**When to use:** When message history needs sender attribution

**Current codebase pattern:**
```csharp
// RedisThreadStateStore persists ChatMessage[] from Microsoft.Extensions.AI
// Needs enhancement to preserve sender metadata alongside role/content
```

**Recommended enhancement:**
- Extend `ChatMessage.AdditionalProperties` to store senderId (non-invasive)
- OR extend `StoreState` wrapper to include sender mapping
- OR use a separate Redis key pattern: `{agentKey}:senders` → `{messageIndex: senderId}`

### Pattern 3: Username in Agent Context
**What:** Pass username (not ID) to agent for natural personalization
**When to use:** Always - agent needs human-readable names for prompts

**Current flow:**
```csharp
// IAgentFactory.Create(agentKey, userId, botTokenHash)
// McpAgent receives userId, uses in prompts
```

**Enhancement:**
- Pass username instead of userId (or both if userId needed elsewhere)
- `CreateRunOptions` already prepends custom instructions - username can be added there

### Anti-Patterns to Avoid
- **Tracking connection IDs in a dictionary:** Use `Context.Items` instead - automatic cleanup, no memory leaks
- **Using Groups for identity:** Groups are for broadcast targeting, not per-connection state
- **Validating senderId against registered identity:** Decision is to trust client - simplifies implementation
- **Persisting senderId in separate table:** Keep attribution with messages for atomic consistency

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-connection state | Dictionary of ConnectionId → Data | `Context.Items` | Automatic lifecycle, no cleanup needed, thread-safe |
| User registration validation | Custom auth tokens | Simple hub method + validation | No JWT needed for app-level identity |
| Message attribution | Custom metadata schema | ChatMessage.AdditionalProperties | Extends existing DTO, JSON serializable |
| Connection cleanup | Manual RemoveFromGroupAsync | Automatic on disconnect | SignalR cleans up Groups automatically |

**Key insight:** SignalR's Context.Items is designed exactly for this use case - per-connection state that lives with the connection and dies with it. Manual tracking leads to memory leaks and race conditions.

## Common Pitfalls

### Pitfall 1: Storing State in Hub Properties
**What goes wrong:** Hub instances are transient - each method call gets a new instance
**Why it happens:** Developers expect hub to be singleton-like
**How to avoid:** Always use `Context.Items` for per-connection state, never hub fields
**Warning signs:** State "disappears" between method calls

### Pitfall 2: Not Validating Registration Before Processing
**What goes wrong:** Unregistered connections send messages, agent receives null/empty username
**Why it happens:** Forgot to guard hub methods with registration check
**How to avoid:** Add registration check at top of every method that needs identity:
```csharp
if (!Context.Items.ContainsKey("UserId"))
    throw new HubException("User not registered");
```
**Warning signs:** Agent receives "web-user" or empty names

### Pitfall 3: Passing User ID Instead of Username to Agent
**What goes wrong:** Agent prompts say "You are talking to alice" instead of "You are talking to Alice"
**Why it happens:** Passing userId directly instead of looking up username
**How to avoid:** Store both ID and username in `Context.Items`, pass username to agent
**Warning signs:** Agent uses IDs in responses

### Pitfall 4: Losing Sender Attribution on Message History Load
**What goes wrong:** Historical messages show no sender when loaded from Redis
**Why it happens:** ChatMessage DTO doesn't persist sender metadata
**How to avoid:** Store senderId in ChatMessage.AdditionalProperties or extend StoreState
**Warning signs:** Sender fields null on history load

### Pitfall 5: Reconnection Loses Registration
**What goes wrong:** User reconnects, registration gone, must call RegisterUser again
**Why it happens:** `Context.Items` clears on disconnect - this is by design
**How to avoid:** Client re-registers on reconnection in `OnReconnected` handler
**Warning signs:** Errors after reconnection saying "User not registered"

## Code Examples

Verified patterns from official sources and codebase analysis:

### Hub Registration Method
```csharp
// Pattern from: https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs
public async Task RegisterUser(string userId)
{
    var users = await LoadUsersFromJsonAsync(); // Server-side users.json
    var user = users.FirstOrDefault(u => u.Id == userId);

    if (user is null)
    {
        throw new HubException($"Invalid user ID: {userId}");
    }

    Context.Items["UserId"] = userId;
    Context.Items["Username"] = user.Username;
    Context.Items["AvatarUrl"] = user.AvatarUrl;
}
```

### Guarded Hub Method
```csharp
public async IAsyncEnumerable<ChatStreamMessage> SendMessage(
    string topicId,
    string message,
    string senderId, // From client
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    // Validate registration
    if (!Context.Items.TryGetValue("Username", out var usernameObj))
    {
        yield return new ChatStreamMessage
        {
            Error = "User not registered. Please register first.",
            IsComplete = true
        };
        yield break;
    }

    var username = usernameObj as string;

    // ... existing SendMessage logic, but pass username to agent
}
```

### Agent Factory with Username
```csharp
// Current: Create(AgentKey agentKey, string userId, string? botTokenHash)
// Enhanced: Pass username instead of userId
DisposableAgent Create(AgentKey agentKey, string username, string? botTokenHash);

// McpAgent.CreateRunOptions already prepends instructions:
// Add username context there
var userContext = $"You are chatting with {username}.";
prompts = prompts.Prepend(userContext);
```

### Persisting SenderId with Messages
```csharp
// Option 1: Use ChatMessage.AdditionalProperties
public async Task SetMessagesAsync(string key, ChatMessage[] messages, Dictionary<int, string> senderIds)
{
    for (int i = 0; i < messages.Length; i++)
    {
        if (senderIds.TryGetValue(i, out var senderId))
        {
            messages[i].AdditionalProperties ??= new();
            messages[i].AdditionalProperties["senderId"] = senderId;
        }
    }

    var json = JsonSerializer.Serialize(new StoreState { Messages = messages });
    await _db.StringSetAsync(key, json, expiration);
}

// Option 2: Extend StoreState
private sealed class StoreState
{
    public ChatMessage[] Messages { get; init; } = [];
    public Dictionary<int, string>? SenderIds { get; init; } // messageIndex → senderId
}
```

### Client Re-registration on Reconnect
```csharp
// WebChat.Client - ChatConnectionService or effect
HubConnection.Reconnected += async _ =>
{
    // Re-register user after reconnection
    var userId = localStorage.GetItem("selectedUserId");
    if (!string.IsNullOrEmpty(userId))
    {
        await HubConnection.InvokeAsync("RegisterUser", userId);
    }

    // ... existing reconnection logic
};
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Manual ConnectionId tracking | Context.Items | ASP.NET Core 1.0+ | Simpler, automatic cleanup |
| Groups for per-user state | Context.Items for state, Groups for broadcast | SignalR design | Clear separation of concerns |
| Hardcoded "web-user" | Dynamic user registration | This phase | Personalized responses |

**Deprecated/outdated:**
- N/A - this is new functionality, not replacing deprecated patterns

## Open Questions

Things that couldn't be fully resolved:

1. **Where should users.json live on server side?**
   - What we know: Currently at `WebChat.Client/wwwroot/users.json` (client-side)
   - What's unclear: Should server duplicate this file, or serve from wwwroot and load via HTTP?
   - Recommendation: Simplest is to duplicate to `Agent/wwwroot/users.json` and load via IWebHostEnvironment, OR embed as resource. Config decision during planning.

2. **Should senderId persistence be in ChatMessage.AdditionalProperties or separate?**
   - What we know: ChatMessage supports AdditionalProperties (JsonExtensionData)
   - What's unclear: Whether this survives serialization round-trip through Redis
   - Recommendation: Test AdditionalProperties first (simplest), fall back to separate dictionary in StoreState if needed. LOW risk.

3. **Should agent receive username only, or full user object?**
   - What we know: Agent currently receives userId string
   - What's unclear: Whether avatar URL or other metadata useful in prompts
   - Recommendation: Pass username only - simplest, most natural for prompts. DECISION can be in planning.

## Sources

### Primary (HIGH confidence)
- [Use hubs in ASP.NET Core SignalR | Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs) - Context.Items, hub lifecycle
- [Manage users and groups in SignalR | Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups) - Groups vs per-connection state
- Codebase analysis: `ChatHub.cs`, `WebChatMessengerClient.cs`, `McpAgent.cs`, `RedisThreadStateStore.cs`

### Secondary (MEDIUM confidence)
- [Managing SignalR ConnectionIds (or why you shouldn't)](https://consultwithgriff.com/signalr-connection-ids) - Best practice to avoid manual tracking
- [SignalR Best Practices | C# Corner](https://www.c-sharpcorner.com/article/signalr-best-practices/) - General patterns

### Tertiary (LOW confidence)
- None - all key findings verified with official Microsoft documentation

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - Using built-in SignalR features, no external dependencies
- Architecture: HIGH - Official Microsoft docs + verified codebase patterns
- Pitfalls: MEDIUM - Based on community experience and common mistakes (not exhaustive testing)

**Research date:** 2026-01-21
**Valid until:** ~60 days (stable ASP.NET Core patterns, unlikely to change)
