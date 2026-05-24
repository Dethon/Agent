# Scheduling as an MCP Server (Channel + Filesystem) — Design Spec

## Goal

Move scheduling out of the agent process into a dedicated `McpServerScheduling` that plays
**two MCP roles over one process**:

1. **A channel** — when a schedule is due, the server fires the prompt as an inbound
   `channel/message`, so a scheduled run flows through the *same* agent-execution pipeline as
   any live user message. This deletes a large duplicated execution path.
2. **A filesystem** — the server publishes `filesystem://schedules`, so the agent creates,
   inspects, edits, reassigns, and deletes schedules with the VFS verbs it already has. No new
   tool schemas; the three bespoke scheduling tools are removed.

The bet is the same one that motivated the HA virtual filesystem: **folding a dissimilar feature
behind interfaces the agent already knows improves agentic performance** — fewer tools to reason
about, one uniform dispatch path, one uniform control surface.

The historical reason scheduling lived in `Domain` (it had to inject prompts into the agent and
there was no channel architecture yet) is now obsolete: channels exist, and a scheduled fire is
structurally identical to an inbound message.

## Why this is the right shape (evidence)

`ScheduleExecutor` and `ChatMonitor` are near-duplicates of one agent-execution loop:

| Step | `ChatMonitor` (live chat) | `ScheduleExecutor` (schedules) |
|---|---|---|
| Get agent | `agentFactory.Create(key, sender, agentId, …)` | `agentFactory.CreateFromDefinition(key, userId, definition, …)` |
| Restore thread | `DeserializeSessionAsync` | `DeserializeSessionAsync` |
| Build user msg | `new ChatMessage(User, content)` + sender + ts | `new ChatMessage(User, prompt)` + sender + ts |
| Run | `RunStreamingAsync().ToUpdateAiResponsePairs()` | `RunStreamingAsync().ToUpdateAiResponsePairs()` |
| Map output | `MapResponseUpdate(...)` | `MapResponseUpdate(...)` |
| Deliver | `channel.SendReplyAsync(...)` | `channel.SendReplyAsync(...)` |

`MapResponseUpdate` is **character-for-character identical** in both files. `ChannelMessage`
already carries `AgentId`, so per-fire agent selection is native to the channel contract. The
only capability the in-process executor has that a channel spoke lacks is **cross-channel
delivery** (it holds `IReadOnlyList<IChannelConnection>` and streams to `defaultScheduleChannelId`,
a *different* channel than the trigger). That single concern is solved by moving reply routing
into the hub (see "Channel contract").

## Constraints and choices

Settled during brainstorming; these drive the rest of the spec:

1. **One combined spec, sequenced internally** — channel migration first, filesystem second.
2. **One process, two MCP roles.** The agent connects to it **twice**: once as a channel (global
   `channelEndpoints`) and once as a tool/filesystem provider (each scheduling-capable agent's
   `mcpServerEndpoints`). Channels and tool/fs servers are discovered through separate config and
   separate connections; this is accepted.
3. **Reply routing lives in the hub, and fans out to multiple targets.** `ChannelMessage` gains an
   optional `ReplyTo` *list* and an optional `Origin`. `ChatMonitor` delivers each streamed update
   to every target.
4. **Scheduled runs auto-approve** (mirroring `McpChannelServiceBus`) — they are autonomous, with
   no human in the loop.
5. **Filesystem is grouped by agent.** `agentId` is implicit in the path, not a body field. Agent
   directories are always listed (even when empty) and each carries a read-only `agent_info.json`.
   Schedule ids are **LLM-chosen, descriptive, and globally unique** slugs.
6. **The schedule store moves into the server.** The cron loop runs there as a `BackgroundService`.

## Architecture

```
                         McpServerScheduling (NEW process, one /mcp endpoint)
                         ┌─────────────────────────────────────────────────┐
  cron tick (30s) ─────▶ │ Dispatcher (BackgroundService)                   │
                         │   due schedules → channel/message notifications  │
                         │ Channel role:  send_reply, request_approval(auto)│
                         │ Filesystem role: fs_* over the schedule store    │
                         │ Store: RedisScheduleStore   CronValidator        │
                         └───────────────┬───────────────────┬─────────────┘
       notifications/channel/message     │                   │  filesystem://schedules + fs_*
       (ConversationId, Content, AgentId,│                   │
        ReplyTo[], Origin)               ▼                   ▼
                            ┌──────────────────────┐   ┌──────────────────────────┐
                            │ McpChannelConnection  │   │ McpFileSystemDiscovery    │
                            │ (channelEndpoints)    │   │ mounts /schedules via     │
                            │ → .Messages stream    │   │ McpFileSystemBackend      │
                            └───────────┬───────────┘   └────────────┬─────────────┘
                                        ▼                            ▼
                            ┌───────────────────────────────────────────────────┐
                            │ Agent (hub)                                         │
                            │  ChatMonitor: run agent, fan replies to ReplyTo[],  │
                            │    emit ScheduleExecutionEvent for Origin=schedule  │
                            │  FileSystemToolFeature: 8 VFS verbs → /schedules    │
                            └───────────────────────────────────────────────────┘
```

The server follows the `McpChannelServiceBus` template: minimal `Program.cs`
(`GetSettings()` → `ConfigureChannel/Filesystem(settings)` → `app.MapMcp("/mcp")`), a hosted
`BackgroundService` for the cron loop, MCP tools for `send_reply` / `request_approval`, and an
MCP resource for `filesystem://schedules`.

## Channel contract changes (multi-target reply-to)

The inbound notification payload and the `ChannelMessage` DTO gain two optional fields:

```csharp
public record ChannelMessage
{
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public required string Sender { get; init; }
    public required string ChannelId { get; init; }
    public string? AgentId { get; init; }
    public IReadOnlyList<ReplyTarget>? ReplyTo { get; init; }   // NEW
    public MessageOrigin? Origin { get; init; }                 // NEW
}

public record ReplyTarget(string ChannelId, string? ConversationId);
public record MessageOrigin(string Kind, string? ScheduleId);  // Kind = "schedule"
```

- **`ReplyTo`** — null/empty ⇒ reply to the originating channel (today's normal-chat behavior,
  unchanged). Each target names a `ChannelId`; a target with no `ConversationId` ⇒ the hub mints
  one via `CreateConversationAsync`.
- **`Origin`** — carries `ScheduleId` so the hub can re-emit schedule metrics (see below).

`McpChannelConnection` parses both fields from the notification. `ChatMonitor`'s delivery step
changes: instead of always `x.Channel.SendReplyAsync(...)`, it resolves the target set, looks each
`IChannelConnection` up by `ChannelId` in the `channels` list it already holds, mints conversations
where a target omits one, and fans every streamed update out to all targets. **Cross-channel
routing stays in the hub — the only component connected to every channel.** When `ReplyTo` is
empty, behavior is byte-for-byte the current behavior.

### Metrics preservation

The Dashboard's Schedules page reads `ScheduleExecutionEvent` history from Redis (via the
Observability service); today those events are published by `ScheduleExecutor`, which this refactor
**deletes**. To keep the analytics whole, `ChatMonitor` emits `ScheduleExecutionEvent` when a run
whose `Origin.Kind == "schedule"` completes — it has the `ScheduleId`, agent, prompt, and can time
the run and capture errors. Normal chat runs are unaffected. Without this step the dashboard
silently goes blank.

## Filesystem control surface

Mount `filesystem://schedules` at `/schedules`, grouped by agent:

```
/schedules/
  <agentId>/                      # one dir per VALID agent — always listed, even empty
    agent_info.json               # { id, name, description } — read-only
    <scheduleId>/                 # LLM-chosen, descriptive, globally-unique slug
      schedule.json   # { prompt, cron | runAt, userId?, deliverTo? }  read + EDIT
      status.json     # { createdAt, lastRunAt, nextRunAt }            read-only
      run_now.sh      # exec → fire immediately
```

`schedule.json` shape (agentId is implicit from the path, never in the body):

```json
{
  "prompt": "Summarize today's tech news and post it to chat",
  "cron": "0 8 * * *",
  "runAt": null,
  "userId": "dethon",
  "deliverTo": ["signalr"]
}
```

- `cron` **XOR** `runAt` (exactly one). `deliverTo` is an optional list of channel ids → becomes
  the fired message's `ReplyTo`; absent ⇒ the configured default target set. `userId` is optional.

Verb mapping (these are the existing 8 VFS verbs — **3 scheduling tools removed, 0 added**):

| Intent | Verb | Path |
|---|---|---|
| List agents | `glob` | `/schedules/*` |
| Learn an agent | `read` | `/schedules/research/agent_info.json` |
| List an agent's schedules | `glob` | `/schedules/research/*` |
| Create | `fs_create` | `/schedules/research/morning-news/schedule.json` (agentId=research, id=morning-news) |
| Inspect | `read` | `.../schedule.json`, `.../status.json` |
| Update prompt/timing | `fs_edit` | `.../schedule.json` (recomputes status) |
| **Reassign agent** | `fs_move` | `/schedules/research/x` → `/schedules/home/x` |
| Delete | `fs_delete` | remove the schedule dir |
| Run now | `fs_exec` | `.../run_now.sh` |
| Find | `fs_search` | grep over ids, prompts, agents |

**Validation on write** (`fs_create` / `fs_edit`), returning the standard error envelope on
failure: `cron` XOR `runAt`; `runAt` in the future; valid cron; agent dir must exist (unknown
agent ⇒ error); `scheduleId` globally unique on create. The schedule is keyed by its global-unique
`scheduleId` and records its `agentId`; the by-agent tree is a *view* derived from that field, so
`fs_move` rewrites the recorded `agentId`.

### Server-knows-agents dependency

Because the server renders `/schedules/<agentId>/` dirs, writes `agent_info.json`, and validates
creation against the valid-agent set, the **scheduling server needs the agent registry**
(ids + names + descriptions) across the process boundary. In-process this came from
`IAgentDefinitionProvider`; the server reads the same agent-definition config. The server needs
only id/name/description, not the full per-agent `mcpServerEndpoints` definitions.

## Components

New code lives in `McpServerScheduling`, decomposed into independently testable units:

1. **Schedule store** — `RedisScheduleStore` (moved from `Infrastructure`), keyed by global-unique
   `scheduleId`, recording `agentId`. CRUD + `GetDueSchedulesAsync` + `UpdateLastRunAsync`.
2. **Cron dispatcher** — `BackgroundService` (replaces `ScheduleDispatcher` + the in-process
   `Channel<Schedule>` + `ScheduleMonitoring`): every 30s, find due schedules, advance
   `nextRun`/`lastRun`, and emit a `channel/message` notification carrying prompt, `agentId`,
   `ReplyTo` (from `deliverTo` or default), and `Origin`. One-shot schedules delete after firing.
3. **Channel emitter** — builds and sends `notifications/channel/message` (cf.
   `ChannelNotificationEmitter`).
4. **Channel MCP tools** — `send_reply` (rarely hit — only when a schedule has empty `deliverTo`,
   in which case output is dropped/logged for v1) and `request_approval` (**auto-approve**).
5. **Filesystem backend** — path model (`Root`, `AgentDir`, `AgentInfoFile`, `ScheduleDir`,
   `ScheduleFile`, `StatusFile`, `RunNowFile`) over in-memory snapshots; `fs_*` wiring for
   `glob`/`read`/`info`/`search`/`create`/`edit`/`delete`/`move`/`exec`; write validation.
6. **Agent registry adapter** — reads agent id/name/description from config to render agent dirs,
   `agent_info.json`, and validate creation.
7. **`filesystem://schedules` resource** — metadata `{ name: "schedules", mountPoint:
   "/schedules", description }`.

`CronValidator` moves alongside the store. The `Schedule` DTO and `ScheduleExecutionEvent` are
retained (the DTO loses nothing; the metric is now emitted by the hub).

## Data flow

- **Fire:** dispatcher finds a due schedule → emits `channel/message` (prompt, agentId, ReplyTo,
  Origin) → `McpChannelConnection` yields a `ChannelMessage` → `ChatMonitor` runs the agent and
  fans replies to every `ReplyTo` target → on completion emits `ScheduleExecutionEvent`.
- **Create:** `fs_create /schedules/research/morning-news/schedule.json` → validate → store →
  `status.json` reflects computed `nextRunAt`.
- **Reassign:** `fs_move /schedules/research/x /schedules/home/x` → rewrite recorded `agentId`.
- **Run now:** `fs_exec .../run_now.sh` → dispatcher enqueues an immediate fire.

## Prompt changes

- Remove the scheduling tools' descriptions and the dynamic agent-list injection
  (`SchedulingToolFeature.BuildCreateDescription`) — the valid agents are now the top-level dirs
  and their `agent_info.json`.
- Teach the filesystem idiom for scheduling-capable agents: glob `/schedules/*` to pick an agent →
  read `agent_info.json` → `fs_create <agent>/<descriptive-id>/schedule.json` to schedule →
  `fs_edit`/`fs_move`/`fs_delete` to manage → `fs_exec run_now.sh` to test.

## What is removed / moved

- **Deleted:** `ScheduleExecutor`, `IScheduleAgentFactory` (+ Infra impl), `ScheduleDispatcher`,
  `ScheduleMonitoring`, the in-process `Channel<Schedule>`, `SchedulingToolFeature`,
  `ScheduleCreateTool` / `ScheduleListTool` / `ScheduleDeleteTool`, and the in-Domain
  `SchedulingModule` wiring.
- **Moved into `McpServerScheduling`:** the cron loop, `RedisScheduleStore`, `CronValidator`,
  validation, the `fs_*` backend, the channel emitter.
- **Kept:** the `Schedule` DTO; `ScheduleExecutionEvent` (now emitted by `ChatMonitor`).
- **Agent-side changes:** `ChannelMessage` + `McpChannelConnection` parse `ReplyTo`/`Origin`;
  `ChatMonitor` gains reply-to fan-out + schedule-origin metric emission. No other agent changes.

## Configuration & infrastructure

Per project rules, the skeleton lands with the code:

- `DockerCompose/docker-compose.yml` — new `mcp-scheduling` service (Redis access for the store).
- `DockerCompose/.env` — only if a new secret is introduced (none expected beyond existing Redis).
- `appsettings.json` / `appsettings.Development.json`:
  - add `mcp-scheduling` to the global `channelEndpoints`;
  - add it to scheduling-capable agents' `mcpServerEndpoints`;
  - default `deliverTo` target set (replaces today's `DefaultScheduleChannelId` = `"signalr"`);
  - the server's Redis connection and agent-registry config.

## Error handling

- **Bad create/edit** (cron XOR runAt, past runAt, invalid cron, unknown agent, duplicate id) →
  standard tool error envelope from the `fs_*` write path.
- **Missing path / unknown schedule** → not-found from `fs_read` / `fs_info`.
- **Exec on a non-`run_now.sh` command** → exit `127` + available-actions listing (cf. HA guard).
- **Fire-time failures** (agent run errors) → surfaced through `ChatMonitor`'s existing error path
  and the re-emitted `ScheduleExecutionEvent` (`Success=false`, `Error`).
- **Redis unreachable** → store errors surfaced as `fs_*` errors / dispatcher logs.

## Testing

TDD throughout — RED test before each unit (project rule). Tests run against fakes (fake store,
fake channel connections, fake agent registry).

**Unit:**
- Schedule store: CRUD, due-query, last-run advance, global-unique id enforcement.
- Cron dispatcher: due selection, next/last advance, one-shot deletion, notification payload
  (ReplyTo from `deliverTo`/default, Origin).
- Filesystem path model: every node type (`AgentDir`, `AgentInfoFile`, `ScheduleDir`,
  `ScheduleFile`, `StatusFile`, `RunNowFile`), including empty agent dirs.
- Write validation: cron XOR runAt, past runAt, unknown agent, duplicate id, cron validity.
- `fs_move` reassigns agent; `fs_exec run_now.sh` enqueues a fire; `127` guard.
- `ChannelMessage` parsing of `ReplyTo` / `Origin`.
- `ChatMonitor` reply-to fan-out (multi-target, mint-conversation, empty ⇒ origin) and
  schedule-origin `ScheduleExecutionEvent` emission.

**Integration:**
- Discovery mounts `filesystem://schedules` at `/schedules`; end-to-end create → glob → read →
  edit → move → delete through the real `FileSystemToolFeature` + `McpFileSystemBackend`.
- End-to-end fire: dispatcher notification → `McpChannelConnection` → `ChatMonitor` → multi-target
  delivery against stub channels.

## Phasing

Auto-commit after each completed RED→GREEN→REVIEW triplet (project rule).

1. **Channel contract** — add `ReplyTo`/`Origin` to `ChannelMessage` + notification parsing;
   `ChatMonitor` fan-out + schedule-origin metric emission. (Agent-side; no behavior change until a
   producer sets the fields.)
2. **Server skeleton + store move** — stand up `McpServerScheduling`, move `RedisScheduleStore` +
   `CronValidator`, host the cron dispatcher, emit `channel/message`. Register as a channel. At this
   point schedules fire through the hub; delete `ScheduleExecutor`/`IScheduleAgentFactory`/
   `ScheduleDispatcher`/`ScheduleMonitoring`.
3. **Filesystem read surface** — `filesystem://schedules`, path model, `fs_glob`/`fs_info`/
   `fs_read` over agent dirs, `agent_info.json`, `schedule.json`, `status.json`, `fs_search`.
4. **Filesystem write + act surface** — `fs_create`/`fs_edit`/`fs_delete`/`fs_move` with
   validation, `fs_exec run_now.sh`. Remove the 3 domain tools + `SchedulingToolFeature`.
5. **Prompt + config cutover** — rewrite the scheduling prompt to the filesystem idiom; compose
   service; `channelEndpoints` / `mcpServerEndpoints` / default `deliverTo`.

## Out of scope (for this spec)

- Per-schedule approval routing to a human (scheduled runs auto-approve in v1).
- Capturing scheduled output into the filesystem when `deliverTo` is empty (v1 drops/logs it; a
  later refinement could surface last-run output via `status.json`).
- A live upcoming-schedule view on the Dashboard (it currently shows execution history only).
- Per-agent (vs global) scheduleId uniqueness — v1 uses global uniqueness.

## Risks

- **Two connections to one process** (channel + tool/fs). Mitigated: same `/mcp` endpoint, distinct
  MCP sessions; documented in config.
- **Metric emission moves to the hub.** If the `Origin` plumbing is wrong, dashboard analytics go
  blank silently. Mitigated by explicit unit + integration coverage of `Origin` → event emission.
- **Cross-process agent registry.** If the server's agent config drifts from the agent's, dirs and
  validation mislead. Mitigated by sourcing both from the same config.
- **`exec` carries a bash prior** for `run_now.sh`. Mitigated by the `127` guard + available-actions
  listing and the mount description.

## Style and layering rules to honour during implementation

- Backend logic in `McpServerScheduling`; Domain-side changes (`ChannelMessage`) keep the Domain
  layer free of Infrastructure/Agent references.
- Modern C#: file-scoped namespaces, primary constructors, `record` DTOs, LINQ over loops,
  `CancellationToken` on all async paths, no XML doc comments.
- No trailing newline in non-test source files; follow existing `Fs*Tool` patterns in
  `McpServerSandbox/McpTools` for MCP tool wrappers and `McpChannelServiceBus` for channel wiring.
