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

- `create_conversation` doubles as the turn-start announce when given `existingConversationId`: ChatMonitor calls it channel-agnostically for agent-initiated messages (`Origin` set) into existing conversations, and each channel applies its own semantics — SignalR opens a live stream + `OnStreamChanged(Started)` before reply chunks arrive; voice no-ops on a live satellite session, else binds the turn as an announcement.
- On connect and every reconnect the agent registers its `AgentCatalogEntry` list via `register_agents` (`ChannelConnectionHost`); channels use this single source instead of duplicated `Agents` config, and SignalR broadcasts `OnAgentsUpdated` so WebChat refreshes live.
- `attachOnly: true` in `ChannelEndpoints` (voice) makes `DeliveryTargetResolver` order that channel last — it attaches to conversations minted elsewhere, never mints the primary target.
- **Dual-role** servers are both a channel and a tool/filesystem server (`mcp-scheduling`, `mcp-library` download alerts); their channel-protocol tools are hidden from the LLM.
- `ChannelMessageNotification.ConfigPatch` (`AgentConfigPatch`: model + reasoning effort) lets a channel override agent config per message; only the SignalR channel populates it. Whitelist: `patchableModels` in `Agent/appsettings.json`, surfaced to clients through the widened `AgentCatalogEntry`.

## Virtual Filesystem Architecture

Each MCP server can expose a `filesystem://` resource (`vault`, `media`, `ha`, `schedules`, `print-queue`, `timers`). At session start `McpFileSystemDiscovery` detects them and mounts them into `VirtualFileSystemRegistry` with longest-prefix path resolution. `FileSystemToolFeature` provides 10 domain tools (`VfsTextRead`, `VfsTextCreate`, `VfsTextEdit`, `VfsGlobFiles`, `VfsTextSearch`, `VfsMove`, `VfsCopy`, `VfsRemove`, `VfsExec`, `VfsFileInfo`) dispatching through the registry; raw MCP `fs_*` tools are filtered out while domain tools are active. `VfsExec` is filesystem-conditional — backends without `fs_exec` return a "tool missing" envelope.

Each mount is its own backend — **tools cannot reach across mounts**; data needed elsewhere must be copied there first. Backends implement `IFileSystemBackend` returning typed `FsResult<T>` (`Ok`/`Err`); besides disk-backed servers, `HaFileSystem`, `ScheduleFileSystem`, `PrinterQueueFileSystem` and `TimerFileSystem` are non-disk backends on the same contract. New filesystems need no agent changes.

Globs support brace expansion (`GlobBraceExpander`): `**/*.{jpg,png}` = union of both patterns (lone/unbalanced `{...}` stay literal). All backends normalize glob entries to full virtual paths.
