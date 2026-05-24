# Type-Safe Channel Protocol — Design Spec

## Goal

The channel protocol — how the Agent (as MCP client) talks to each channel MCP server
(SignalR, Telegram, ServiceBus, Scheduling) — is held together by **string convention**, not
shared types. Field names are declared independently on each side and agree only by luck. A typo
fails silently: a misnamed key deserializes to `null`, a dropped field vanishes. One such
divergence already exists in production code (see below).

The fix: a **single shared protocol surface in `Domain/DTOs/Channel/`** — typed payload records for
notifications, the existing param DTOs used on *both* sides for tools, one `ChannelProtocol` type
holding the method/tool names, serializer options, and the serialize/deserialize helpers. After this
change, adding or renaming a wire field is a compile-time change in one place, not a convention to
re-establish on five.

## Why this shape

Every channel server already references `Domain` (directly, or via `Infrastructure`), and shared
param DTOs (`SendReplyParams`, `RequestApprovalParams`, `CreateConversationParams`,
`AgentCatalogEntry`, `ToolApprovalRequest`) already exist and are used on the **consumer** side. The
gap is that publishers bypass them — hand-writing dictionaries and anonymous objects — and that
notifications have no shared type at all. This design closes the gap by routing every crossing
through the types that already exist (plus two new notification records), rather than inventing a new
abstraction. The MCP transport is unchanged; only the C# that constructs and reads payloads changes.

## The convention-coupling today (confirmed by reading the code)

| Crossing | Publisher | Consumer | Coupling |
|---|---|---|---|
| `channel/message` | SignalR/Telegram/ServiceBus each build their own **anonymous object** `{ConversationId, Sender, Content, AgentId, Timestamp}`; Scheduling builds a **local `SchedulePayload` record** (only one carrying `replyTo`/`origin`) | `McpChannelConnection.HandleChannelMessageNotification` hand-parses `JsonElement` by string key | 4 independent shapes ↔ 1 hand parser |
| `channel/cancel` | SignalR builds anonymous `{ConversationId, AgentId, Timestamp}` | `HandleChannelCancelNotification` hand-parses | by name |
| `send_reply` | Agent hand-builds `Dictionary<string,object?>` keys | MCP-typed flat method params → builds `SendReplyParams` | dict keys ↔ param names |
| `request_approval` | Agent `Dictionary` + `requests` **double-serialized to a JSON string** | flat params; SignalR `Deserialize<List<ToolApprovalRequest>>(string)`; **Telegram deserializes into a local `ToolRequest(ToolName, Arguments)` that silently drops `MessageId`** | string blob + divergent inner type |
| `register_agents` | Agent `Dictionary` + `agents` **double-serialized to a JSON string** | SignalR `Deserialize<List<AgentCatalogEntry>>(string)` | string blob |
| `create_conversation` | Agent hand-builds `Dictionary` keys | flat params → builds `CreateConversationParams` | dict keys ↔ param names |

The Telegram `ToolRequest` divergence is a latent correctness bug, not a style nit: an auto-approval
notification routed through Telegram loses the `MessageId` grouping field that SignalR preserves.

## Constraints and choices

Settled during brainstorming; these drive the spec:

1. **Full scope** — notifications *and* tools, plus a shared `ChannelProtocol` module. The entire
   wire format gets one source of truth.
2. **Native-typed nested lists.** `requests` and `agents` stop being double-serialized JSON strings.
   They become real array-of-object tool parameters; MCP deserializes them natively. This deletes
   the Telegram local `ToolRequest` and makes `MessageId` survive. The two tools' input schema
   changes from `string` to an array — acceptable because these are internal agent↔channel tools,
   never user-facing.
3. **Reuse existing param DTOs; do not collapse tool params into a single object parameter.** Tool
   methods keep flat, idiomatic MCP parameters. The publisher stops hand-writing keys by building the
   shared param DTO and serializing it to the args dictionary. The residual coupling (DTO property
   names ↔ method parameter names, both camelCase, both in our control) is pinned by a test.
4. **`ChannelMessage` stays the Agent-internal domain type.** It carries `ChannelId` (local context,
   set by the receiving connection — not wire data). A separate `ChannelMessageNotification` record
   is the wire type; the consumer deserializes it then maps to `ChannelMessage`, adding `ChannelId`.
5. **String-enum wire format preserved.** `contentType` (`"Text"`) and `mode` (`"Request"`) currently
   travel as strings. The shared `ChannelProtocol.SerializerOptions` includes `JsonStringEnumConverter`
   so behavior is byte-identical.
6. **Notification handlers stay resilient.** The consumer runs notification callbacks fire-and-forget.
   After the change, a malformed/missing-field payload is logged and skipped rather than throwing —
   no harder failure than today.

## Architecture

```
Domain/DTOs/Channel/  (shared by Agent + every channel server)
├── ChannelProtocol          ← NEW: name constants, SerializerOptions, ToArguments/Deserialize
├── ChannelMessageNotification ← NEW: wire record for channel/message
├── ChannelCancelNotification  ← NEW: wire record for channel/cancel
├── SendReplyParams            (exists; now used on publisher side too)
├── RequestApprovalParams      (Requests: string → IReadOnlyList<ToolApprovalRequest>)
├── CreateConversationParams   (exists; now used on publisher side too)
├── AgentCatalogEntry          (exists; register_agents param becomes typed list)
└── ToolApprovalRequest        (exists; now the only approval-request shape, incl. Telegram)

Agent (client)                         Channel servers (publisher of notifications,
─────────────                           consumer of tools)
McpChannelConnection                   ChannelNotificationEmitter (×3) / ScheduleNotificationEmitter
  notifications:  Deserialize<…>         build ChannelMessageNotification / ChannelCancelNotification
  tools:          ToArguments(dto)     McpTools/*: [McpServerTool(Name = ChannelProtocol.*)]
                                        request_approval / register_agents take typed list params
```

### Data flow

- **Notifications (server → agent):** the emitter constructs the shared record and calls
  `SendNotificationAsync(ChannelProtocol.MessageNotification, record)`. The MCP server serializes it
  (camelCase). The Agent's handler does `ChannelProtocol.Deserialize<ChannelMessageNotification>(element)`,
  and on a valid result maps to `ChannelMessage` (adding the connection's `ChannelId`) and writes it
  to the channel; on null/invalid it logs and skips.
- **Tools (agent → server):** `McpChannelConnection` builds the shared param DTO, calls
  `ChannelProtocol.ToArguments(dto)` to produce the `IReadOnlyDictionary<string,object?>` argument
  set, and invokes the tool by its `ChannelProtocol` name constant. MCP deserializes each argument
  into the tool's flat parameters (scalars) or typed list parameters (`requests`, `agents`).

## Components

### Domain (shared) — `Domain/DTOs/Channel/`

- **`ChannelProtocol.cs`** (NEW, static class) — the single source of truth:
  - Name constants (`const string`): `MessageNotification = "notifications/channel/message"`,
    `CancelNotification = "notifications/channel/cancel"`, `SendReplyTool = "send_reply"`,
    `RequestApprovalTool = "request_approval"`, `CreateConversationTool = "create_conversation"`,
    `RegisterAgentsTool = "register_agents"`. Usable in `[McpServerTool(Name = …)]` because they are
    compile-time constants.
  - `SerializerOptions` — `new JsonSerializerOptions(JsonSerializerDefaults.Web)` (camelCase +
    case-insensitive) plus `JsonStringEnumConverter`. Used for notification (de)serialization and for
    `ToArguments`.
  - `ToArguments<T>(T dto) : IReadOnlyDictionary<string, object?>` — `SerializeToElement(dto,
    SerializerOptions)` then project top-level properties to a dictionary (`JsonElement` values),
    suitable for `CallToolAsync`.
  - `Deserialize<T>(JsonElement element) : T?` — `element.Deserialize<T>(SerializerOptions)`.
- **`ChannelMessageNotification.cs`** (NEW) — `record { ConversationId, Sender, Content, AgentId?,
  IReadOnlyList<ReplyTarget>? ReplyTo, MessageOrigin? Origin, DateTimeOffset Timestamp }`. Replaces
  the three anonymous objects and the local `SchedulePayload`.
- **`ChannelCancelNotification.cs`** (NEW) — `record { ConversationId, AgentId?, DateTimeOffset Timestamp }`.
- **`RequestApprovalParams.cs`** (CHANGED) — `Requests` type `string` → `IReadOnlyList<ToolApprovalRequest>`.
- **`RegisterAgentsParams.cs`** (NEW) — `record { IReadOnlyList<AgentCatalogEntry> Agents }`. So
  `register_agents` routes through `ToArguments` like every other tool, with no hand-written key.

### Agent (client) — `Infrastructure/Clients/Channels/McpChannelConnection.cs`

- Replace the local notification-method `const string`s with `ChannelProtocol.*`.
- `HandleChannelMessageNotification` / `HandleChannelCancelNotification`: replace hand-parsing with
  `ChannelProtocol.Deserialize<…>` + map to `ChannelMessage`; guard null/invalid (log + skip).
- `SendReplyAsync` / `RequestApprovalAsync` / `NotifyAutoApprovedAsync` / `CreateConversationAsync` /
  `RegisterAgentsAsync`: build the shared param DTO (`SendReplyParams`, `RequestApprovalParams`,
  `CreateConversationParams`, `RegisterAgentsParams`) and call `ChannelProtocol.ToArguments(...)`
  instead of hand-built dictionaries and `JsonSerializer.Serialize(...)` string blobs. Invoke tools by their
  `ChannelProtocol` name constant. `RequestApprovalParams` now carries the typed list directly
  (no `JsonSerializer.Serialize(requests)`).

### Channel servers — publishers (notification emitters)

- **SignalR / Telegram / ServiceBus `Services/ChannelNotificationEmitter.cs`** — replace the
  anonymous `channel/message` and `channel/cancel` objects with `ChannelMessageNotification` /
  `ChannelCancelNotification`; send via the `ChannelProtocol` name constants.
- **`McpServerScheduling/Services/ScheduleNotificationEmitter.cs`** — delete the local
  `SchedulePayload` record; `BuildPayload`/`EmitAsync` use `ChannelMessageNotification`.

### Channel servers — consumers (tools), all four servers

- `McpTools/*Tool.cs`: use `[McpServerTool(Name = ChannelProtocol.*)]` for every channel tool.
- **`request_approval`** — parameter `string requests` → `IReadOnlyList<ToolApprovalRequest> requests`;
  build `RequestApprovalParams` with the typed list.
  - **Telegram** — delete the local `ToolRequest` record; use `ToolApprovalRequest` (so `MessageId`
    is retained); `FormatApprovalMessage` takes `IReadOnlyList<ToolApprovalRequest>`.
  - **SignalR `ApprovalService`** — `RequestApprovalAsync`/`NotifyAutoApprovedAsync` use
    `p.Requests` directly; delete `DeserializeRequests`.
  - **ServiceBus / Scheduling** — already ignore the payload; just update the parameter type.
- **`register_agents`** (SignalR only today) — parameter `string agents` →
  `IReadOnlyList<AgentCatalogEntry> agents`; drop the internal `JsonSerializer.Deserialize`. (Publisher
  builds `RegisterAgentsParams`; the single `agents` parameter matches its one property.)
- **`send_reply` / `create_conversation`** — unchanged signatures; only the `Name` constant.

## Wire contract (after change)

`channel/message` notification params:

```jsonc
{
  "conversationId": "c1", "sender": "scheduler", "content": "run it", "agentId": "jonas",
  "replyTo": [{ "channelId": "signalr", "conversationId": null }],
  "origin": { "kind": "schedule", "scheduleId": "morning-news" },
  "timestamp": "2026-05-24T08:00:00Z"
}
```

`request_approval` tool arguments (note: `requests` is now an array, not a string):

```jsonc
{
  "conversationId": "c1", "mode": "Request",
  "requests": [{ "messageId": "m1", "toolName": "mcp__x__do", "arguments": { "k": "v" } }]
}
```

`register_agents` tool arguments: `{ "agents": [{ "id": "jack", "name": "Jack", "description": "…" }] }`.

## Error handling

- **Notification deserialize failure / missing required field:** `Deserialize` returns null or throws
  `JsonException`; the handler catches, logs a warning, and skips (the message channel is never
  written a malformed entry). No harder failure than today's `GetProperty` throw.
- **Tool errors** remain centralized in each server's `AddCallToolFilter` → `ToolResponse.Create(ex)`;
  tool wrappers carry no try/catch (per `.claude/rules/mcp-tools.md`).
- **Tool absence** (`create_conversation`, `register_agents` on servers that don't expose them):
  unchanged — the Agent probes `ListToolsAsync` and skips.

## Testing (TDD, Shouldly + Moq, no trailing newline in `.cs`)

- **`ChannelProtocol`** — `ToArguments` round-trips each param DTO (keys camelCase, enums as strings,
  optional fields handled); `Deserialize` round-trips both notification records incl. optional
  `replyTo`/`origin`.
- **Param-name alignment** — a test asserting each tool's `ToArguments` key set equals that tool's
  parameter names (the one residual convention, pinned).
- **`McpChannelConnectionParsingTests`** — keep the existing `HandleChannelMessageNotification` cases
  (they exercise the new deserialize path); add a malformed-payload case proving log-and-skip.
- **`ScheduleNotificationPayloadTests`** — assert the emitter now produces `ChannelMessageNotification`
  with the expected camelCase wire shape (`replyTo`, `origin.kind`, `origin.scheduleId`).
- **Telegram approval** — a test proving `MessageId` survives end-to-end through `request_approval`
  (the regression the local `ToolRequest` caused).
- **Integration** — the existing `McpSchedulingServerFixture` notification path still round-trips.

## Out of scope

- The MCP transport, notification/tool *names*, and the set of tools each server exposes — unchanged.
- Collapsing tool parameters into a single object parameter (rejected in choice 3).
- Any change to `ChannelMessage`'s role as the Agent-internal domain type or to how the message
  channel is consumed downstream.
