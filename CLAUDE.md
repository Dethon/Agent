# Ziggurat

AI agent via Telegram/WebChat/MessageBus using .NET 10 LTS, MCP, and OpenRouter LLMs. The solution file is
`Ziggurat.sln`.

`satellite/` is `nabu-satellite`, a standalone Rust crate outside the .NET solution — read `satellite/CLAUDE.md` before touching it.

## Verify Before Assuming

Before proposing any architectural change or debugging hypothesis, verify your assumptions against the actual state (read the file, run the command, check the config). Never assume something is missing or broken without evidence.

## Communication Style

Use plain language and short sentences, in replies and in docs. Avoid jargon and overly compressed phrasing.

## Build, Test & Format

- `Tests/Unit` runs standalone. `Tests/Integration` and E2E tests (`[Trait("Category", "E2E")]`) need Docker, but their fixtures spin up the containers themselves (testcontainers for integration, the compose stack for E2E) — just run `dotnet test`; set `PLAYWRIGHT_HEADLESS=false` to watch the browser.
- The pre-commit hook (`.githooks/pre-commit`, wired via `core.hooksPath`) runs `dotnet format` over staged `.cs` files and re-stages them **whole** — partial/hunk staging does not survive a commit; make the working tree match the commit you want.
- `.editorconfig` sets `insert_final_newline = false`: `.cs` files have **no trailing newline**.

## Rules & TDD

`.claude/rules/*.md` are path-scoped (frontmatter `paths:`) and apply when touching matching files — coding style and layer rules, plus per-subsystem architecture notes (voice, printing, timers, scheduling, observability, memory, web browsing, Home Assistant, OpenRouter provider routing). Don't duplicate their content here.

Follow Red-Green-Refactor for all features and bug fixes: write a failing test first, watch it fail, then implement.

## Environment Variables

New configuration lives in `appsettings.json` / `appsettings.Development.json` by default. `DockerCompose/docker-compose.yml`'s `environment` block and `DockerCompose/.env` are not a mirror of every setting — they exist only for:

- **Secrets** (API keys, connection strings, credentials) — placeholder entry in `DockerCompose/.env` (never a real value), wired into `docker-compose.yml` as `${VAR_NAME}`.
- **Non-generic parameters** — inherently per-deployment values (a satellite's host IP, a topology-dependent URL) — a `docker-compose.yml` environment entry (placeholder like `changeme` where there's no safe default).

A new generic tunable (threshold, window, feature flag) belongs in `appsettings.json` **alone**. When adding code that reads a new setting, update whichever category applies in the same change.

## Multi-Agent Patterns

- **Stuck workers**: replace, don't wait — spawn a fresh agent for the same task. Never retry the same failing action more than twice; after two failures reassess or escalate.
- **Layer completion**: check `TaskList` for `completed` on every task in a layer before starting dependent work or reporting success. Never infer completion from partial signals.
- **Auto-commit** after each TDD triplet (RED → GREEN → REVIEW) succeeds, with a message referencing the triplet's feature.

## Local Development

Compose files, the launch command, secrets mounts, and WebChat/Dashboard access live in the `launch-stack` skill (`.claude/skills/launch-stack/SKILL.md`). Home Assistant setup lives in `.claude/rules/home-assistant.md`.

## Channel Architecture

Transports (WebChat, Telegram, ServiceBus, Voice, Scheduling) are independent MCP channel servers; the agent connects as an MCP client via `ChannelEndpoints`. Wire serialization is centralized in `ChannelProtocol` (shared `JsonSerializerOptions` + typed records — `ChannelMessageNotification`, `ChannelCancelNotification`, `RegisterAgentsParams`, `RequestApprovalParams`). Inbound: `channel/message`, `channel/cancel`. Outbound tools: `send_reply`, `request_approval`, `create_conversation`, `register_agents`. A new transport needs only a new channel server — zero agent changes.

- **Being a channel server is one call.** `Channels.Hosting`'s `IMcpServerBuilder.AddChannelServer(policy, subscriberId?, errorResult?)` wires the `ChannelInbox`, the shared `channel_receive` long-poll tool, the call-tool filter (cancellation propagates; anything else becomes an error result) and the sealed `ChannelNotificationEmitter`. A new transport writes only its reply-sending logic. The project references Domain and the MCP server package alone, never Infrastructure — two channel servers depend on Domain only and must stay that way.
- **`DeliveryPolicy` is a required argument.** `Broadcast` always enqueues, so an idle-but-unpruned subscriber still receives (SignalR, Voice). `BufferAlways` targets a known subscriber id and creates its queue on demand, for a transport that cannot tell a sender to retry (Telegram). `GateOnLive` enqueues only when someone is actually polling, for callers that settle a durable record on a confirmed delivery — buffering on a failed emit would keep the record *and* leave a duplicate (ServiceBus, Scheduling, Library).
- **Liveness is the return value of emitting**, never a separate property: `EmitAsync`/`EmitCancelAsync` answer "was anyone listening?", with the freshness window internal to `ChannelInbox`. The same stale-subscriber defect was fixed three times across six hand-copied emitters before this. `Tests/Integration/Channels/ChannelReceiveContractTests.cs` boots every real `ConfigModule` and asserts its declared policy.

- `create_conversation` doubles as the turn-start announce when given `existingConversationId`: ChatMonitor calls it channel-agnostically for agent-initiated messages (`Origin` set) into existing conversations, and each channel applies its own semantics — SignalR opens a live stream + `OnStreamChanged(Started)` before reply chunks arrive; voice no-ops on a live satellite session, else binds the turn as an announcement.
- On connect and every reconnect the agent registers its `AgentCatalogEntry` list via `register_agents` (`ChannelConnectionHost`); channels use this single source instead of duplicated `Agents` config, and SignalR broadcasts `OnAgentsUpdated` so WebChat refreshes live.
- `attachOnly: true` in `ChannelEndpoints` (voice) makes `DeliveryTargetResolver` order that channel last — it attaches to conversations minted elsewhere, never mints the primary target.
- **Dual-role** servers are both a channel and a tool/filesystem server (`mcp-scheduling`, `mcp-library` download alerts); their channel-protocol tools are hidden from the LLM.
- `ChannelMessageNotification.ConfigPatch` (`AgentConfigPatch`: model + reasoning effort) lets a channel override agent config per message; only the SignalR channel populates it. Whitelist: `patchableModels` in `Agent/appsettings.json`, surfaced to clients through the widened `AgentCatalogEntry`.

## Virtual Filesystem Architecture

Each MCP server can expose a `filesystem://` resource (`vault`, `media`, `ha`, `schedules`, `print-queue`, `timers`). At session start `McpFileSystemDiscovery` detects them and mounts them into `VirtualFileSystemRegistry` with longest-prefix path resolution. `FileSystemToolFeature` provides 10 domain tools (`VfsTextRead`, `VfsTextCreate`, `VfsTextEdit`, `VfsGlobFiles`, `VfsTextSearch`, `VfsMove`, `VfsCopy`, `VfsRemove`, `VfsExec`, `VfsFileInfo`) dispatching through the registry; raw MCP `fs_*` tools are filtered out while domain tools are active. `VfsExec` is filesystem-conditional — backends without `fs_exec` return a "tool missing" envelope.

Each mount is its own backend — **tools cannot reach across mounts**; data needed elsewhere must be copied there first. Backends derive from `FileSystemBackendBase` (`Domain/Contracts/`), which implements `IFileSystemBackend`'s twelve operations as unsupported and provides what every backend used to copy: the error envelopes, the glob prologue (base path plus the trailing-slash dirs-only rule), a search regex compiled with a match timeout and guarded against a bad pattern, and the search template. New filesystems need no agent changes.

- **A backend declares its capability by overriding.** `AddFileSystemTools<TBackend>()` (`Infrastructure/Utils/FileSystemServerTools.cs`) reflects over which methods `TBackend` overrides and registers an `fs_*` tool for exactly those, taking each description from the backend's `Describe*` hook. Nothing is declared, so nothing can drift — a server cannot advertise an operation its backend does not implement. Never hand-write an `fs_*` MCP tool. `Tests/Unit/Infrastructure/FileSystemServerConformanceTests.cs` asserts, per server, that advertised tools, overridden operations and published capabilities are one set.
- **Capability is per operation, not per path.** A backend may override an operation and still refuse particular paths; the list tells the model which operations exist on a mount, not which will succeed on a given file. Do not refine it into a per-path check the registrar cannot answer.
- **`FileSystemOperations.All` (`Domain/Contracts/`) is the one list.** The registrar, `FsResultContract.ResultTypes`, the discovery capability map, `ThreadSession`'s filter set and `FileSystemToolFeature.AllToolKeys` all derive from it, so a new operation cannot half-exist.
- **Disk roots are `DiskFileSystem`** (glob, info, read, move, delete, copy, blob read/write), `TextDiskFileSystem` (adds create, edit, text search where a root has allowed extensions) and `SandboxFileSystem` (adds exec). The library composes a `DownloadsOverlay` onto the same root for its downloads view, so it stays a composition rather than a root-path wrapper. Containment is decided once, by `PathJail`.
- **Resolution is data.** `IVirtualFileSystemRegistry.Resolve` returns `FsResult<FileSystemResolution>`; an unmounted path becomes the error envelope the prompt promises, at every tool site.

Globs support brace expansion (`GlobBraceExpander`): `**/*.{jpg,png}` = union of both patterns (lone/unbalanced `{...}` stay literal). All backends normalize glob entries to full virtual paths.

## Agent skills

### Issue tracker

Issues and specs live as tracked markdown under `.scratch/<feature-slug>/` — not GitHub Issues, despite the remote. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, each label string equal to its name, written as a `Status:` line in the issue file. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `docs/adr/` at the root, plus a `CONTEXT.md` created lazily when a term needs pinning down. See `docs/agents/domain.md`.
