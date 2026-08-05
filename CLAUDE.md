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

## MCP Server Hosting

`Mcp.Hosting` holds what being an MCP server means, so no server hand-writes it. The project
references Domain, the MCP server package and the configuration binder alone, never Infrastructure —
two channel servers depend on Domain only and must stay that way.

- **`IConfigurationBuilder.BindSettings<TSettings>()` is the only way a server reads configuration.**
  Environment variables first, user secrets last, so **user secrets win** — deliberately, and the
  reverse of the framework default. Read `docs/adr/0005-user-secrets-outrank-environment-variables.md`
  before touching the order; reversing it silently switches off CapSolver, web push and the Music
  Assistant action on every containerised deployment. The secrets id comes off the entry assembly, so
  the five servers with no `UserSecretsId` simply have no such source. Nested sections bind through
  the plain call. A `required` member that bound to null fails startup naming it; **null only, never
  empty** — six shipped servers carry required members that ship as `""` and are filled from secrets
  (ServiceBus, Telegram, WebSearch, HomeAssistant, Idealista, Library).
- **`IServiceCollection.AddMcpHost(settings)`** is the three things every server has: the settings
  singleton, the server and the HTTP transport. All thirteen use it.
- **`AddToolServer(settings, errorResult?)`** is the host plus the call-tool error filter, for the
  nine servers that offer the agent things to call. Being a tool server and being a channel server
  are independent, so a dual-role server calls `AddToolServer` and then `AddChannelServer`.
- **The error filter is one shared registration, installed at most once.** A cancelled call
  propagates as the abort it is; anything else is logged and becomes the caller's error result. Two
  filters nested around each other would let the outer one convert the very cancellation the inner
  rethrows, so a second ask is a no-op and the first ask's error shape wins.
- **`Tests/Integration/McpServers/McpServerRegistrations.cs` is the one server table.** Thirteen
  rows, each driving the real `ConfigModule`; `McpServerContractTests` asserts every server resolves
  its settings as a singleton, registered the host and has exactly one call-tool filter. A new server
  is one new row.

## Channel Architecture

Transports (WebChat, Telegram, ServiceBus, Voice, Scheduling) are independent MCP channel servers; the agent connects as an MCP client via `ChannelEndpoints`. Wire serialization is centralized in `ChannelProtocol` (shared `JsonSerializerOptions` + typed records — `ChannelMessageNotification`, `ChannelCancelNotification`, `RegisterAgentsParams`, `RequestApprovalParams`). Inbound: `channel/message`, `channel/cancel`. Outbound tools: `send_reply`, `request_approval`, `create_conversation`, `register_agents`. A new transport needs only a new channel server — zero agent changes.

- **Being a channel server is one call.** `Mcp.Hosting`'s `IMcpServerBuilder.AddChannelServer(policy, subscriberId?, errorResult?)` wires the `ChannelInbox`, the shared `channel_receive` long-poll tool, the call-tool error filter and the sealed `ChannelNotificationEmitter`. It sits beside `AddToolServer`, and the difference between the two kinds of server is exactly what those two calls differ by. A new transport writes only its reply-sending logic.
- **`DeliveryPolicy` is a required argument.** `Broadcast` fans out to every registered subscriber whatever its freshness, so an idle-but-unpruned subscriber still receives; with no subscriber registered at all the item is discarded (SignalR, Voice). `BufferAlways` targets a known subscriber id and creates its queue on demand, for a transport that cannot tell a sender to retry (Telegram). `GateOnLive` enqueues only when someone is actually polling, for callers that settle a durable record on a confirmed delivery — buffering on a failed emit would keep the record *and* leave a duplicate (ServiceBus, Scheduling, Library).
- **Liveness is the return value of emitting**, never a separate property: `EmitAsync`/`EmitCancelAsync` answer "was anyone listening?", with the freshness window internal to `ChannelInbox`. The same stale-subscriber defect was fixed three times across six hand-copied emitters before this. `ChannelReceiveContractTests` drives the channel-capable rows of that table and asserts each declared policy.

- **A conversation group is anchored and built by its first turn.** `ConversationGroup` (internal to `Domain/Monitor`, one per conversation and agent, constructed only by `ChatMonitor`) owns the pending-turn queue, the command dispatch, the delivery anchors, the agent, the restored thread and the warmup, and establishes all of it on the group's **first turn**. A chat command is not a turn, so a group whose messages are all commands builds nothing — a `/clear` on a conversation with no live group costs no agent, no thread read and no MCP connection. The thread context stays eager because disposing it is how a command ends the group; `ChatThreadResolver.ClearAsync` wipes persisted state unconditionally. Whether a turn reuses the anchors is decided by identity against the anchor message, never by a message counter. See `docs/adr/0006-a-group-is-anchored-and-built-by-its-first-turn.md`. The monitor keeps merging the channel streams, grouping the messages, delivering each update and publishing first-reply latency.
- `DeliveryTarget.Minted` means "minted while resolving this turn", so reused anchors carry it cleared and `AnnounceTurnStartAsync` needs no correcting flag: it skips exactly the targets marked minted.
- `create_conversation` doubles as the turn-start announce when given `existingConversationId`: the conversation group calls it channel-agnostically for agent-initiated messages (`Origin` set) into existing conversations, and each channel applies its own semantics — SignalR opens a live stream + `OnStreamChanged(Started)` before reply chunks arrive; voice no-ops on a live satellite session, else binds the turn as an announcement.
- **A channel connection runs itself.** `IMcpChannelConnection.RunAsync(endpoint, catalog, ct)` owns connect with retry, register the catalog, watch health, reconnect with retry and re-register; `ChannelConnectionHost` reads the endpoint map and starts one run per endpoint, and knows nothing else about the order. Being **not connected** is five behaviours, one per member, stated on that interface (`docs/adr/0011-not-connected-is-five-behaviours-and-stays-that-way.md`) — `CreateConversationAsync`'s null is load-bearing for `DeliveryTargetResolver`. The far end's tool set is asked for once per **connection generation** and discarded on reconnect (`docs/adr/0012-a-servers-tool-set-is-fixed-for-a-connection-generation.md`).
- On connect and every reconnect the agent registers its `AgentCatalogEntry` list via `register_agents`; channels use this single source instead of duplicated `Agents` config, and SignalR broadcasts `OnAgentsUpdated` so WebChat refreshes live.
- `attachOnly: true` in `ChannelEndpoints` (voice) makes `DeliveryTargetResolver` order that channel last — it attaches to conversations minted elsewhere, never mints the primary target.
- **Dual-role** servers are both a channel and a tool/filesystem server (`mcp-scheduling`, `mcp-library` download alerts); their channel-protocol tools are hidden from the LLM.
- `ChannelMessageNotification.ConfigPatch` (`AgentConfigPatch`: model + reasoning effort) lets a channel override agent config per message; only the SignalR channel populates it. Whitelist: `patchableModels` in `Agent/appsettings.json`, surfaced to clients through the widened `AgentCatalogEntry`.

## Virtual Filesystem Architecture

Each MCP server can expose a `filesystem://` resource (`vault`, `media`, `ha`, `schedules`, `print-queue`, `timers`). At session start `McpFileSystemDiscovery` detects them and mounts them into `VirtualFileSystemRegistry` with longest-prefix path resolution. `FileSystemToolFeature` provides 10 domain tools (`VfsTextRead`, `VfsTextCreate`, `VfsTextEdit`, `VfsGlobFiles`, `VfsTextSearch`, `VfsMove`, `VfsCopy`, `VfsRemove`, `VfsExec`, `VfsFileInfo`) dispatching through the registry; raw MCP `fs_*` tools are filtered out while domain tools are active. `VfsExec` is filesystem-conditional — backends without `fs_exec` return a "tool missing" envelope.

Each mount is its own backend — **tools cannot reach across mounts**; data needed elsewhere must be copied there first. Backends derive from `FileSystemBackendBase` (`Domain/Contracts/`), which implements `IFileSystemBackend`'s twelve operations as unsupported and provides what every backend used to copy: the error envelopes, the glob prologue (base path plus the trailing-slash dirs-only rule), a search regex compiled with a match timeout and guarded against a bad pattern, and the search template. New filesystems need no agent changes.

- **A backend declares its capability by overriding.** `AddFileSystemTools<TBackend>()` (`Infrastructure/Utils/FileSystemServerTools.cs`) reflects over which methods `TBackend` overrides and registers an `fs_*` tool for exactly those, taking each description from the backend's `Describe*` hook. Nothing is declared, so nothing can drift — a server cannot advertise an operation its backend does not implement. Never hand-write an `fs_*` MCP tool. `Tests/Unit/Infrastructure/FileSystemServerConformanceTests.cs` asserts, per server, that advertised tools, overridden operations and published capabilities are one set.
- **A mount's identity comes from the backend too.** `AddFileSystemResource<TBackend>()` (`Infrastructure/Utils/FileSystemServerResource.cs`) sits beside the tool registrar and publishes the `filesystem://` resource, deriving the address, the published name and the mount point from the backend's one `FilesystemName`, with the prose from its `DescribeMount` hook. They cannot disagree, so there is nothing to keep in sync. Never hand-write a filesystem resource. `DescribeMount` is abstract on the base and satisfied by a constructor argument on the generic disk root, exactly as the name already is — otherwise "Obsidian vault" ends up hardcoded into a reusable type.
- **Capability is per operation, not per path.** A backend may override an operation and still refuse particular paths; the list tells the model which operations exist on a mount, not which will succeed on a given file. Do not refine it into a per-path check the registrar cannot answer.
- **`FileSystemOperations.All` (`Domain/Contracts/`) is the one list.** The registrar, `FsResultContract.ResultTypes`, the discovery capability map, `ThreadSession`'s filter set and `FileSystemToolFeature.AllToolKeys` all derive from it, so a new operation cannot half-exist.
- **Disk roots are `DiskFileSystem`** (glob, info, move, delete, copy, blob read/write — no read, because reading bytes as text needs a rule about which files are text), `TextDiskFileSystem` (adds read, create, edit and text search where a root has allowed extensions), `SandboxFileSystem` (adds exec) and `MediaLibraryDiskFileSystem` (adds the `DownloadsOverlay`: a virtual `downloads/<id>/status.json` per active download, delete that cancels the download, and a refusal to move or write that virtual file). The generic disk root knows about none of them. Containment is decided once, by `PathJail`.
- **Resolution is data.** `IVirtualFileSystemRegistry.Resolve` returns `FsResult<FileSystemResolution>`; an unmounted path becomes the error envelope the prompt promises, at every tool site.

Globs support brace expansion (`GlobBraceExpander`): `**/*.{jpg,png}` = union of both patterns (lone/unbalanced `{...}` stay literal). All backends normalize glob entries to full virtual paths.

## Agent skills

### Issue tracker

Issues and specs live as tracked markdown under `.scratch/<feature-slug>/` — not GitHub Issues, despite the remote. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles, each label string equal to its name, written as a `Status:` line in the issue file. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `docs/adr/` at the root, plus a `CONTEXT.md` created lazily when a term needs pinning down. See `docs/agents/domain.md`.
