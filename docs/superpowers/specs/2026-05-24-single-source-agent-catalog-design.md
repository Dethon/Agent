# Single-Source Agent Catalog via Channel Registration — Design Spec

## Goal

Make `Agent/appsettings.json` the **only** place agents are defined. Today agent identity
(`Id`, `Name`, `Description`) is declared in three places that drift independently:

| Where | Type | Used for |
|---|---|---|
| `Agent/appsettings.json` | `AgentDefinition` (authoritative) | everything (model, endpoints, features, …) |
| `McpServerScheduling/appsettings.json` | `SchedulingAgentConfig { Id, Name, Description }` | `fs_create` validation + `agent_info.json` |
| `McpChannelSignalR/appsettings.json` | `AgentConfig { Id, Name, Description }` | `ChatHub.GetAgents()` (WebChat selector) + `IsValidAgent()` |

The drift is already real: SignalR's `appsettings.json` describes Jack as *"Download assistant"*
and Jonas as *"General assistant"*, independent of the authoritative definitions. Adding or
renaming an agent today means editing up to three files.

The fix: the **Agent process announces its catalog** to each channel server when it connects.
Channel servers stop declaring agents in config and instead hold a runtime-populated catalog.
`AgentDefinition` already carries `Id`, `Name`, and `Description`, so the announced payload is a
one-line projection of data the Agent already owns.

## Why this shape

The channel architecture already inverts control this way: the Agent is the MCP **client** of each
channel server and pushes data to it via tool calls (`send_reply`, `request_approval`,
`create_conversation`). `create_conversation` is the direct precedent for this work — it is a
**standard but optional** channel tool that only some servers implement; `McpChannelConnection`
probes for it via `ListToolsAsync` and silently skips servers that lack it. Catalog registration
follows the same contract: define the tool once, call it uniformly on every channel connection,
let each server opt in.

This keeps the channel protocol stable (one tool contract, uniform Agent behavior) without forcing
servers that have no use for the catalog (Telegram, ServiceBus) to implement anything.

## Constraints and choices

Settled during brainstorming; these drive the rest of the spec:

1. **Cross-agent scheduling stays supported.** A schedule may fire as an agent other than the one
   that authored it, so the catalog must hold the full agent set — it cannot collapse to "who am I".
2. **Runtime registration over the existing channel connection** (not a shared Redis key, not a
   shared mounted file). `Agent/appsettings.json` is the single source of truth; deployment files
   are untouched.
3. **`register_agents` is a standard, optional, probed channel tool.** The Agent calls it
   identically on every channel `Connect`/`Reconnect`, skipping servers that don't expose it —
   exactly like `create_conversation`.
4. **Implemented now by Scheduling and SignalR.** Both have the duplicated config today. Telegram
   and ServiceBus adopt the same contract later with zero Agent changes.
5. **SignalR broadcasts catalog updates.** WebChat connects to the SignalR server, not the Agent,
   so it would not otherwise see a re-registration. On `register_agents`, SignalR emits an
   `OnAgentsUpdated` hub event **carrying the updated catalog**; WebChat dispatches `SetAgents`
   with that payload and refreshes its selector directly (no extra round-trip). Initial load still
   uses `GetAgents` on connect.
6. **One canonical catalog DTO.** The two near-twin DTOs (`ScheduleAgentInfo`,
   `Domain.DTOs.WebChat.AgentInfo`) collapse into a single `AgentCatalogEntry`.
7. **Empty catalog before registration / when the Agent is down is acceptable** — in both states
   the Agent is unreachable, so there are no valid agents to offer or schedule for.

## Architecture

```
Agent process                          Channel server (Scheduling / SignalR)
─────────────                          ─────────────────────────────────────
AgentSettings.Agents                   MutableAgentCatalog (singleton)
   │ project                              ▲          │
   ▼                                      │ Replace  │ GetAll/Get/Exists
IReadOnlyList<AgentCatalogEntry>          │          ▼
   │ inject                            RegisterAgentsTool   consumers:
   ▼                                      ▲                  - Scheduling: fs_create validation,
ChannelConnectionHost                     │                                agent_info.json
   │ after Connect/Reconnect              │ call             - SignalR: GetAgents/IsValidAgent,
   ▼                                      │                              AgentsUpdated broadcast
McpChannelConnection.RegisterAgentsAsync ─┘
   (probe ListTools → call register_agents → swallow absence/errors)
```

### Data flow

1. At startup `ChannelConnectionHost` connects each channel, then calls
   `conn.RegisterAgentsAsync(catalog, ct)`.
2. `McpChannelConnection.RegisterAgentsAsync` probes `ListToolsAsync` for `register_agents`. If
   absent (Telegram/ServiceBus), it returns. Otherwise it calls the tool with
   `agents = JsonSerializer.Serialize(catalog)` and catches `McpException` (registration never
   tears down a channel).
3. The server's `RegisterAgentsTool` deserializes the payload and calls
   `MutableAgentCatalog.Replace(entries)`.
4. SignalR additionally broadcasts `OnAgentsUpdated` carrying the catalog; WebChat dispatches
   `SetAgents` with the payload.
5. On health-check failure `ChannelConnectionHost` reconnects and re-registers, so a channel-server
   restart self-heals.

## Components

### Domain (shared)

- **`Domain/DTOs/Channel/AgentCatalogEntry.cs`** — `record AgentCatalogEntry(string Id, string Name,
  string? Description)`. Canonical wire + domain DTO. Replaces `ScheduleAgentInfo` and
  `Domain.DTOs.WebChat.AgentInfo`.
- **`IAgentCatalog`** — `IReadOnlyList<AgentCatalogEntry> GetAll()`, `AgentCatalogEntry? Get(string id)`,
  `bool Exists(string id)`, `void Replace(IReadOnlyList<AgentCatalogEntry> agents)`. Replaces
  `IScheduleAgentCatalog`.
- **`MutableAgentCatalog`** — thread-safe implementation (atomic reference swap on `Replace`).
  Both servers register it as a singleton. Replaces `ScheduleAgentCatalog`.

### Agent (client)

- **`IMcpChannelConnection` / `McpChannelConnection`** — add
  `Task RegisterAgentsAsync(IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct)`:
  probe-and-call, swallow absence and `McpException`, mirroring `CreateConversationAsync`.
- **`ChannelConnectionHost`** — take the projected catalog via constructor; after each successful
  `ConnectWithRetryAsync` and `ReconnectWithRetryAsync`, call `RegisterAgentsAsync`.
- **`InjectorModule`** — project `settings.Agents` → `AgentCatalogEntry[]` and inject into
  `ChannelConnectionHost`.

### Scheduling server

- **`McpTools/RegisterAgentsTool.cs`** — `register_agents`; deserializes `agents` and calls
  `catalog.Replace(...)`; returns ack (count). No try/catch (error filter handles it).
- **`ScheduleFileSystem`** — depends on `IAgentCatalog` instead of `IScheduleAgentCatalog`
  (validation + `agent_info.json` rendering unchanged in behavior).
- **`ConfigModule`** — register `MutableAgentCatalog` as the `IAgentCatalog` singleton + add the tool.
- **Removals:** `SchedulingSettings.Agents`, `SchedulingAgentConfig`, `ScheduleAgentCatalog`
  (service), and the `Agents` block in `McpServerScheduling/appsettings.json`.

### SignalR server

- **`McpTools/RegisterAgentsTool.cs`** — `register_agents`; `catalog.Replace(...)`, then broadcast
  `OnAgentsUpdated` (carrying the catalog) via `IHubNotificationSender`, fire-and-forget.
- **`ChatHub`** — `GetAgents()` returns `IReadOnlyList<AgentCatalogEntry>` from the catalog;
  `ValidateAgent()` reads from the catalog.
- **DI** — register `MutableAgentCatalog` as the `IAgentCatalog` singleton + add the tool.
- **Removals:** `ChannelSettings.Agents`, `AgentConfig`, and the `Agents` block in
  `McpChannelSignalR/appsettings.json`.

### WebChat client

- Handle the `OnAgentsUpdated` hub event (via the existing `HubEventDispatcher`) → dispatch
  `SetAgents(payload)` → update agent-selector state. Initial load still uses `GetAgents` on connect.
- Update references from `Domain.DTOs.WebChat.AgentInfo` to `AgentCatalogEntry` (serialization is
  byte-identical, so connected clients are unaffected).

## Wire contract

`register_agents` tool:

```jsonc
// request
{ "agents": "[{\"id\":\"jack\",\"name\":\"Jack\",\"description\":\"...\"},{\"id\":\"jonas\",...}]" }
// response (text)
"registered 2 agents"
```

`OnAgentsUpdated` hub event (SignalR → WebChat): payload is the updated catalog
(`IReadOnlyList<AgentCatalogEntry>`), which WebChat applies via `SetAgents`.

## Error handling

- **Registration failure** (tool throws, server unreachable mid-call): caught in
  `RegisterAgentsAsync`, logged, swallowed. The channel connection stays up; re-registration occurs
  on the next reconnect.
- **Tool absence:** the probe skips it; no error.
- **Empty catalog reads:** Scheduling `fs_create` rejects unknown `agentId` (existing validation
  path); `agent_info.json` lists none; WebChat selector is empty. All acceptable per choice 7.
- **Server-side tool errors** are formatted by the existing `AddCallToolFilter` →
  `ToolResponse.Create(ex)`; the tool wrappers carry no try/catch (per `.claude/rules/mcp-tools.md`).

## Testing (TDD, Shouldly + Moq, no trailing newline in `.cs`)

- **Domain:** `MutableAgentCatalog` — `Replace` swaps the set; `Get`/`Exists`/`GetAll` reflect it;
  concurrent replace/read is safe. `AgentCatalogEntry` (de)serialization round-trips.
- **Agent client:** `RegisterAgentsAsync` calls `register_agents` with the serialized catalog when
  present; skips when the tool is absent; swallows `McpException`.
- **Scheduling:** `RegisterAgentsTool` populates the catalog; after registration, `fs_create`
  validation accepts a registered `agentId` and `agent_info.json` lists registered agents (extend
  the existing `McpSchedulingServerFixture` integration test).
- **SignalR:** `RegisterAgentsTool` updates the catalog and emits `AgentsUpdated`; `GetAgents`
  reflects the registered set; `IsValidAgent` validates against it.

## Out of scope

- Telegram and ServiceBus channel servers (they keep skipping the probed tool; adopt later).
- Any change to `AgentDefinition`'s other fields or to how agents are otherwise loaded.
- Persisting the catalog across an Agent outage (intentional — empty-when-down is correct).
