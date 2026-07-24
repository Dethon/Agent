# Agent

AI agent via Telegram/WebChat/MessageBus using .NET 10 LTS, MCP, and OpenRouter LLMs.

## Verify Before Assuming

Before proposing any architectural change or debugging hypothesis, first verify your assumptions by checking the actual current state (read the file, run the command, check the config). Never assume something is missing or broken without evidence.

## Projects

| Project | Purpose |
|---------|---------|
| `Agent` | Composition root, DI, connects to channel and tool MCP servers |
| `Domain` | Contracts, DTOs, business logic |
| `Infrastructure` | External clients, agent implementations, push notifications |
| `McpServer*` | MCP tool servers (Library, Vault, Sandbox, WebSearch, Idealista, HomeAssistant, Printer, Scheduling) |
| `McpChannel*` | MCP channel servers — each bridges a transport to the agent |
| `McpChannelSignalR` | WebChat/SignalR channel — hosts SignalR hub, streams, approvals, push notifications |
| `McpChannelTelegram` | Telegram channel — multi-bot polling (one bot per agent), inline keyboard approvals |
| `McpChannelServiceBus` | Azure Service Bus channel — queue processor, auto-approval |
| `McpChannelVoice` | Voice channel — Wyoming hub that dials hardware satellites, Lemonade STT/TTS (OpenAI-compatible), follow-up windows, announcements; dual-role: exposes `filesystem://timers` (hub-local countdown timers that ring insistently) |
| `McpServerScheduling` | Scheduling server — dual-role: `filesystem://schedules` VFS + channel that fires due schedules as `channel/message` |
| `McpServerPrinter` | Printer server — `filesystem://print-queue` VFS that submits copied/created files to a configured IPP/CUPS printer |
| `WebChat`/`.Client` | Blazor WebAssembly chat interface, Redux-like state (Stores + Effects + HubEventDispatcher) |
| `Observability` | Metrics collector, REST API, SignalR hub — serves the Dashboard PWA |
| `Dashboard.Client` | Blazor WebAssembly observability dashboard (token costs, tool analytics, errors, schedules, memory, health) |
| `satellite` | `nabu-satellite` — standalone Rust crate (NOT in the .NET solution); see `satellite/CLAUDE.md` |
| `Tests` | Unit, integration, and E2E tests |

## Key File Locations

| What | Where |
|------|-------|
| Contracts | `Domain/Contracts/*.cs` |
| DTOs | `Domain/DTOs/*.cs` |
| Agent implementations | `Infrastructure/Agents/*.cs` |
| External clients | `Infrastructure/Clients/**/*.cs` |
| ChatMonitor & reply fan-out | `Domain/Monitor/*.cs` — `ChatMonitor`, `DeliveryTargetResolver`, `ReplyDispatcher`, `FirstReplyTracker`, `DeliveryTarget` |
| MCP tool server tools | `McpServer*/McpTools/*.cs` |
| Channel MCP tools & services | `McpChannel*/McpTools/*.cs`, `McpChannel*/Services/*.cs` |
| Channel protocol DTOs | `Domain/DTOs/Channel/*.cs` (`ChannelProtocol.cs` centralizes wire serialization) |
| Agent catalog | `Domain/Agents/MutableAgentCatalog.cs`, `Domain/Contracts/IAgentCatalog.cs`, `Domain/DTOs/Channel/AgentCatalogEntry.cs` |
| WebChat state | `WebChat.Client/State/**/*.cs` |
| Dashboard | `Dashboard.Client/{Pages,Components,Services}/`, state in `Dashboard.Client/State/**/*.cs` |
| Metrics | DTOs `Domain/DTOs/Metrics/*.cs` (dimension/metric enums in `Enums/`), publisher `Infrastructure/Metrics/*.cs` |
| Observability | `Observability/Services/*.cs` (incl. `MetricsQueryService.cs`), API endpoints `Observability/MetricsApiEndpoints.cs` |
| Subagents | `Domain/Tools/SubAgents/*.cs`, `Domain/Prompts/SubAgentPrompt.cs`, `Domain/DTOs/SubAgent*.cs`, DI `Agent/Modules/SubAgentModule.cs` |
| Memory | `Infrastructure/Memory/*.cs`, `Domain/Tools/Memory/*.cs`, extraction queue `Domain/Memory/*.cs`, `Domain/Contracts/IMemory*.cs`, `Domain/Prompts/MemoryPrompts.cs`, DI `Agent/Modules/MemoryModule.cs` |
| Filesystem (VFS) tools | `Domain/Tools/FileSystem/*.cs` (incl. `GlobBraceExpander.cs`, `GlobRegex.cs`) |
| Filesystem contracts & DTOs | `Domain/Contracts/IFileSystem*.cs`, `Domain/Contracts/IVirtualFileSystemRegistry.cs`, `Domain/DTOs/FileSystemMount.cs`, `Domain/DTOs/FileSystem/*.cs` |
| VFS registry & backends | `Infrastructure/Agents/VirtualFileSystemRegistry.cs`, `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs` + `McpFileSystemDiscovery.cs`, `Infrastructure/Clients/LocalFileSystemClient.cs` |
| Filesystem MCP resources | `McpServer{Vault,Library,Sandbox,HomeAssistant,Printer,Scheduling}/McpResources/FileSystemResource.cs` |
| Home Assistant VFS engine | `Domain/Tools/HomeAssistant/Vfs/*.cs` |
| Scheduling | `McpServerScheduling/**/*.cs`, VFS engine `Domain/Tools/Scheduling/Vfs/*.cs`, `Domain/Prompts/SchedulingPrompt.cs`, `Domain/DTOs/Schedule.cs` |
| Printing | `McpServerPrinter/**/*.cs`, engine `Domain/Tools/Printing/{,Vfs/}*.cs`, `Domain/Prompts/PrintingPrompt.cs`, contracts `Domain/Contracts/IPrinterClient.cs` + `IPrintSpool.cs`, DTOs `Domain/DTOs/Printing/*.cs`, IPP client `Infrastructure/Clients/Printer/*.cs`, spool `Infrastructure/Printing/PrintSpool.cs` |
| Web browsing | `Domain/Tools/Web/*.cs`, `Domain/Prompts/WebBrowsingPrompt.cs`, `Domain/Contracts/IWebBrowser.cs`, `Infrastructure/Clients/Browser/*.cs` |
| Satellite (Rust) | `satellite/src/**/*.rs` — key files, invariants, build & WSL scripts in `satellite/CLAUDE.md` |
| Tests | `Tests/{Unit,Integration}/**/*Tests.cs`, E2E `Tests/E2E/{Dashboard,WebChat}/*E2ETests.cs`, fixtures `Tests/E2E/Fixtures/*.cs` |

## Build, Test & Format

```bash
dotnet build agent.sln
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorTests"
```

- `Tests/Unit` runs standalone. `Tests/Integration` needs the Docker services it touches (most need `redis`). E2E tests (`[Trait("Category", "E2E")]`) need the full compose stack up; set `PLAYWRIGHT_HEADLESS=false` to watch the browser.
- The pre-commit hook (`.githooks/pre-commit`, wired via `core.hooksPath`) runs `dotnet format` over staged `.cs` files and re-stages them **whole** — partial/hunk staging does not survive a commit; make the working tree match the commit you want.
- `.editorconfig` sets `insert_final_newline = false`: `.cs` files have **no trailing newline**.

## Rules & TDD

`.claude/rules/*.md` are path-scoped (frontmatter `paths:`) and apply when touching matching files: `dotnet-style.md` (all C#), `domain-layer.md`, `infrastructure-layer.md`, `mcp-tools.md`, `testing.md`, `nuget.md`. Don't duplicate their content here.

Follow Red-Green-Refactor for all features and bug fixes: write a failing test first, watch it fail, then implement.

## Environment Variables

When adding code that reads new environment variables or configuration values, you **must** update all relevant infrastructure files in the same change:

- `DockerCompose/docker-compose.yml` — add the variable to the appropriate service's `environment` section (use placeholder values like `${VAR_NAME}` or `changeme`).
- `DockerCompose/.env` — add a placeholder entry for new **secrets only** (API keys, connection strings, credentials). Non-secret configuration belongs in `appsettings.json`, not `.env`.
- `appsettings.json` / `appsettings.Development.json` — add the corresponding configuration key with a placeholder value.

Do not defer these updates to a later step. The skeleton must exist at the same time the code that maps the variable is created.

## Multi-Agent Patterns

### Handling Agent Failures

- If a worker agent appears stuck (no progress after a reasonable period), **replace it** — spawn a fresh agent for the same task rather than retrying the stuck one indefinitely.
- Do not retry the same failing action more than twice. After two failures, reassess the approach or escalate to the user.
- When a worker reports an error, the orchestrator should decide whether to reassign, adjust the task, or abort — not blindly re-dispatch.

### Layer Completion Verification

Before marking a layer of work as done, **verify every agent in that layer has completed**. Do not assume completion from partial signals. Check `TaskList` to confirm all tasks in the layer are `completed` before proceeding to dependent work or reporting success.

### Auto-Commit After Triplets

When executing TDD plans with triplet tasks (RED → GREEN → REVIEW), **commit after each triplet completes successfully**. This keeps the history granular and makes rollbacks cheap. The commit message should reference the triplet's feature or task name.

## Local Development

### Docker Compose files

| File                                                | Purpose |
|-----------------------------------------------------|---------|
| `DockerCompose/docker-compose.yml`                  | Main service definitions |
| `DockerCompose/docker-compose.override.windows.yml` | Windows user secrets mount (`%APPDATA%/Microsoft/UserSecrets`) |
| `DockerCompose/docker-compose.override.linux.yml`   | Linux user secrets mount (`$HOME/.microsoft/usersecrets`) |
| `DockerCompose/docker-compose.override.no-dri.yml`  | Strips the `/dev/dri` device from `plex`/`mcp-sandbox`/`lemonade` (and forces `lemonade` STT to CPU) on hosts without a DRI render node (e.g. NVIDIA-only WSL2) |

### Launching

Pick the override file matching your OS:

```bash
# Linux / WSL
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer lemonade tse-extractor qbittorrent jackett redis caddy camoufox homeassistant music-assistant

# Windows
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.windows.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer lemonade tse-extractor qbittorrent jackett redis caddy camoufox homeassistant music-assistant
```

The base compose maps `/dev/dri` into `plex`/`mcp-sandbox` (and `lemonade`, for Vulkan whisper) for GPU hardware acceleration. Only `/dev/dri` is mapped — the shipped Vulkan tier needs just the render node, so a host with `/dev/dri` but no `/dev/kfd` (Intel iGPU, Raspberry Pi) still comes up; `/dev/kfd` (ROCm compute) is never mapped — the iGPU runs Vulkan/RADV, not ROCm. The opt-in NPU tier (`docker-compose.override.npu.yml`) instead maps `/dev/accel/accel0`, the XDNA NPU node. Hosts **without** a DRI render node (NVIDIA-only WSL2 has `/dev/dxg` + the NVIDIA Container Toolkit, never `/dev/dri`) will fail with `error gathering device information while adding custom device "/dev/dri"`. On those hosts, append `-f DockerCompose/docker-compose.override.no-dri.yml` as the last `-f` to strip the device (it also drops `lemonade` to the CPU STT tier). (The VS Code `docker-debug-up` task already includes this override — debug never needs the GPU.)

### Secrets

Services read secrets from .NET User Secrets mounted into containers at `/home/app/.microsoft/usersecrets`. The override files map the host-side path. If the agent crashes with `Value cannot be an empty string. (Parameter 'connectionString')`, user secrets are not being mounted — check you're using the correct override for your OS.

### Accessing the WebChat

Caddy (port 443, Let's Encrypt TLS) is the entry point. It routes `/hubs/*` to the McpChannelSignalR hub, `/dashboard/*` to the Observability service, and everything else to the WebUI. **Connect through Caddy, not directly to webui:5001**, or SignalR won't reach the channel server.

### Accessing the Dashboard

The observability dashboard is available at `https://assistants.herfluffness.com/dashboard/` (via Caddy) or `http://localhost:5003/dashboard/` (direct). It's a PWA that can be installed as a standalone app. The dashboard shows token costs, tool analytics, error rates, schedule history, memory analytics, and live service health. Data flows via Redis Pub/Sub: services emit metric events → the Observability collector aggregates them → the dashboard reads via REST API and receives live updates via SignalR.

### Accessing Home Assistant

Home Assistant runs at `http://<host>:8123` (port published on all interfaces so you can configure it from any LAN machine). On first run:

1. Create the owner account through the browser onboarding flow.
2. From the user profile menu, open **Security → Long-Lived Access Tokens** and create one.
3. Set `HOMEASSISTANT__TOKEN=...` in `DockerCompose/.env` and restart the `mcp-homeassistant` container.
4. To control the Roborock S8: Settings → Devices & Services → Add Integration → **Roborock**, log in with the Roborock account; the vacuum appears as `vacuum.<name>` once the integration finishes.

For voice alarms/reminders, the agent creates events on a dedicated `calendar.assistant_alarms`
calendar that an HA automation bridges to the voice announce endpoint — see
`docs/home-assistant-alarms.md` for the one-time `rest_command` + automation provisioning.

The agent reaches HA inside the compose network at `http://homeassistant:8123` via the `McpServerHomeAssistant` MCP server.

### Observability Architecture

Services publish `MetricEvent` DTOs via `IMetricsPublisher` → Redis Pub/Sub channel `metrics:events`. The `MetricsCollectorService` subscribes, aggregates into Redis (sorted sets for time-series, hashes for totals, TTL keys for health), and forwards live events to the SignalR hub (`/hubs/metrics`). `MetricsQueryService` provides grouped aggregation queries over the stored metrics (breakdowns by dimension/metric enums). The dashboard uses a hybrid approach: REST API for historical data on page load, SignalR for real-time updates. Dashboard components (`DynamicChart`, `PillSelector`) use `LocalStorageService` to persist UI state across sessions.

### Memory Architecture

Memory is a built-in agent feature (not a separate MCP server). It runs as services inside the Agent process:
- **Extraction**: `ChatMonitor` queues conversation turns → `MemoryExtractionWorker` processes the queue, fetches the persisted thread, and slices a conversation window anchored at the recall point → `IMemoryExtractor` (LLM-based) receives the windowed context (formatted by `ConversationWindowRenderer` with `[CURRENT]`/`[context -N]` markers) and identifies memories to store → `IMemoryStore` (Redis Stack with vector search) persists them. Falls back to direct message content when the thread is unavailable.
- **Recall**: `MemoryRecallHook` runs before each agent turn, builds a user-only conversation window from the persisted thread for context, sets an anchor index for extraction, retrieves relevant memories via semantic search, and injects them into the system prompt.
- **Dreaming**: `MemoryDreamingService` periodically consolidates and prunes memories using `IMemoryConsolidator` (LLM-based).
- **Metrics**: Extraction, recall, and dreaming events are published as `MetricEvent` DTOs for the Observability dashboard.

### Channel Architecture

Transports (WebChat, Telegram, ServiceBus, Voice, Scheduling) run as independent MCP channel servers. The agent connects to them as an MCP client via `ChannelEndpoints` config. Each channel exposes a standard protocol; wire serialization is centralized in `ChannelProtocol` (shared `JsonSerializerOptions` plus typed notification/param records — `ChannelMessageNotification`, `ChannelCancelNotification`, `RegisterAgentsParams`, `RequestApprovalParams`):
- **Inbound**: `channel/message` notification (user message → agent), `channel/cancel` notification
- **Outbound**: `send_reply` tool (agent response → user), `request_approval` tool (tool approval flow), `create_conversation` tool (agent-initiated conversations; with `existingConversationId` it doubles as the turn-start announce — ChatMonitor calls it channel-agnostically for agent-initiated messages (`Origin` set) into existing conversations, and each channel applies its own semantics: SignalR sets up a live stream + `OnStreamChanged(Started)` before reply chunks arrive; voice no-ops when the satellite session is live and otherwise binds the turn as an announcement), `register_agents` tool (agent publishes its catalog to the channel)

On connect and after every reconnect, the agent registers its agent catalog (an `AgentCatalogEntry` list) with each channel via `register_agents` (`ChannelConnectionHost`). Channels consume this single-source catalog instead of duplicated `Agents` config — SignalR broadcasts `OnAgentsUpdated`, so WebChat refreshes its agent list live.

A channel endpoint can declare `attachOnly: true` in `ChannelEndpoints` (voice does): `DeliveryTargetResolver` orders attach-only channels last when resolving fan-out delivery targets, so they attach to conversations minted elsewhere but are never the primary minted target.

A server can be **dual-role** — both a channel (in `ChannelEndpoints`) and a tool/filesystem server (in an agent's `mcpServerEndpoints`); `mcp-scheduling` and `mcp-library` (download-completion alerts) are both. For dual-role servers the channel-protocol tools (`send_reply`, `request_approval`, `register_agents`) are hidden from the LLM.

New transports can be added by deploying a new channel MCP server — zero agent changes needed.

### Voice Satellite Architecture

Voice is an MCP channel server (`McpChannelVoice`, channelId `voice`, container `mcp-channel-voice`, port 6015) plus hardware satellites. The hub is the Wyoming-protocol **client**: `WyomingSatelliteHost` dials out to every satellite that has an `Address` in `VoiceSettings.Satellites` (`Satellites__<id>__Address`, e.g. `tcp://192.168.5.55:10800`) and reconnects forever; address-less satellites stay in the catalog as announce targets but are never dialed (announcements to them report offline). Pipeline: satellite wakes locally → streams mic `audio-chunk`s → `SatelliteSession`/`SilenceGate` segment the utterance → Lemonade STT (`lemonade`, OpenAI `/v1/audio/transcriptions`, Whisper-Medium on whisper.cpp; device via `STT_BACKEND` ∈ cpu|gpu — or optionally the experimental NPU tier through Lemonade's `flm` recipe, enabled by `docker-compose.override.npu.yml` + `STT_MODEL`; decode quality via `STT_VAD_THRESHOLD`/`STT_INITIAL_PROMPT`/`STT_BEAM_SIZE` — entrypoint defaults Silero VAD 0.6 + Castilian initial prompt + beam 5, empty disables, NPU/flm tier ignores them) → transcript dispatched as `channel/message` → agent reply synthesized by Lemonade Kokoro (`/v1/audio/speech`, streamed 24 kHz PCM resampled in-hub to 22 050 Hz) → streamed back as `audio-start`/`audio-chunk`/`audio-stop`. Sending a `transcript` event to the satellite ends its turn and re-arms wake; `FollowUpConversation` can reopen the mic wake-free, announced by the `ListeningChime` earcon.

The satellite is `nabu-satellite` (`satellite/` — a standalone Rust crate, not in the .NET solution). **Its invariants, build/deploy, and the WSL dev-satellite scripts (`scripts/wsl-satellite.sh`, `scripts/wsl-satellite-winaudio.sh`) are documented in `satellite/CLAUDE.md`** — read it before touching either side of the wire. What the hub side must respect: the satellite is the Wyoming **server** (the hub dials in); its playback sink is FIXED 22 050 Hz mono S16LE and ignores announced rates, so all hub-emitted audio (TTS, `ListeningChime`) must be 22 050 Hz; the dockerized hub dials the dev satellite addresses only under `ASPNETCORE_ENVIRONMENT=Development` (`McpChannelVoice/appsettings.Development.json` overrides exactly the `Satellites` addresses; production config points at the Pi IPs).

### Scheduling Architecture

Scheduling is a dual-role MCP server (`McpServerScheduling`), not an in-process agent feature. It exposes:
- **A `filesystem://schedules` resource** (mount `/schedules`) — the agent manages schedules with the standard `domain__filesystem__*` tools. Layout: `/schedules/<agentId>/<scheduleId>/schedule.json` (`{prompt, cron|runAt, userId?, deliverTo?}` — exactly one of `cron` recurring or `runAt` one-shot), plus `agent_info.json` and read-only `status.json` (`createdAt`/`lastRunAt`/`nextRunAt`). `fs_exec run_now.sh` on a schedule directory fires it immediately. The `ScheduleFileSystem` engine (`Domain/Tools/Scheduling/Vfs/`) implements `IFileSystemBackend` and returns typed `FsResult<T>`.
- **A channel** — `ScheduleDispatcherService` (BackgroundService) polls `IScheduleStore` for due schedules, uses `ScheduleFirePlanner` to choose delete-after-fire (one-shot `runAt`) vs. update-next-run (recurring `cron`), and emits a `channel/message` to the agent via `ScheduleNotificationEmitter`. The agent runs the prompt and `ChatMonitor` fans the result out to the schedule's `deliverTo` channels, minting conversations as needed.

The `scheduling_prompt` MCP prompt (`Domain/Prompts/SchedulingPrompt.cs`) teaches the LLM the `/schedules` idiom. The old in-process path (`ScheduleDispatcher`, `ScheduleExecutor`, dedicated `Schedule*Tool`s, `SchedulingToolFeature`) was removed.

### Printing Architecture

Printing is a non-disk MCP filesystem server (`McpServerPrinter`), not an in-process agent feature — same shape as `McpServerScheduling`. It exposes a **`filesystem://print-queue` resource** (mount `/print-queue`) backed by `PrinterQueueFileSystem` (`Domain/Tools/Printing/Vfs/`), an `IFileSystemBackend` returning typed `FsResult<T>`. Copying or creating a file into `/print-queue/<filename>` (bytes ingest via `fs_blob_write` chunk streaming) immediately submits it to a single configured printer; `fs_delete` on a still-active job cancels it; `move` and `exec` are unsupported. The engine depends on two contracts:
- **`IPrinterClient`** — `IppPrinterClient` (`Infrastructure/Clients/Printer/`) is a `SharpIppNext` + `HttpClient` adapter against `PRINTERURI` (CUPS server or direct-IPP printer), mapping `Print-Job`/`Get-Jobs`/`Cancel-Job`. `IppJobStateMapper` maps IPP job states to domain `PrintJobState`. Get-Jobs requests `job-state` so active jobs aren't pruned mid-print, and `GetActiveJobsAsync` defensively drops non-active states (via `IppJobStateMapper.IsActive`) for printers that ignore `WhichJobs.NotCompleted`.
- **`IPrintSpool`** — `PrintSpool` (`Infrastructure/Printing/`) is a disk-backed store under the spool volume (mount `/spool`), keyed by filename, holding `{JobId, ContentType, Bytes, SubmittedAt, MissingSince}` so `read`/`search`/`edit`/blob read-back work while a job is active. Pruned during reconciliation by `PrintQueueCoordinator`, which **debounces absence**: a submitted job is pruned only after it has stayed absent from the printer's active set past `ReconcileGraceMilliseconds`, so a just-submitted job the printer hasn't registered yet (or a transient empty `Get-Jobs` response) isn't dropped mid-print.

**The accepted formats are configurable via `SupportedFormats`** (default `text,jpeg,pwg-raster,urf,pcl` — plain text, JPEG, and the printer-native raster/page formats); anything else is rejected on copy-in. `SupportedFormats` is the single source of truth: `PrintingPrompt.Build`/`DescribeFormats` and the print-queue resource description derive their advertised format list from it, so whatever is accepted is exactly what the agent is told it can print (no drift). Submission is `application/octet-stream` (IPP printers reject unknown content types otherwise); text payloads are CRLF-normalized (content-sniffed for octet-stream copies) to stop staircase printing, and images use `print-scaling=fit`. Spool blobs use the `.blob` suffix. `PrintableContent` (`Domain/Tools/Printing/`) handles format detection/normalization. The `printing_prompt` MCP prompt (`Domain/Prompts/PrintingPrompt.cs`) teaches the LLM the accepted formats and to convert anything else first. `/print-queue/status.json` is a read-only view of queued jobs; finished jobs auto-disappear from the listing.

### Virtual Filesystem Architecture

The agent exposes a unified virtual filesystem across MCP servers. Each MCP server can expose a `filesystem://` resource (e.g., `filesystem://vault`, `filesystem://media`, `filesystem://ha`, `filesystem://schedules`, `filesystem://print-queue`). At session start, `McpFileSystemDiscovery` detects these resources and mounts them into a `VirtualFileSystemRegistry` with longest-prefix path resolution. `FileSystemToolFeature` provides 10 domain tools (`VfsTextRead`, `VfsTextCreate`, `VfsTextEdit`, `VfsGlobFiles`, `VfsTextSearch`, `VfsMove`, `VfsCopy`, `VfsRemove`, `VfsExec`, `VfsFileInfo`) that dispatch through the registry. `VfsExec` is filesystem-conditional — backends that don't implement `fs_exec` return a "tool missing" envelope when invoked. Raw MCP `fs_*` tools are filtered out when domain tools are active. Each mount is its own backend — tools cannot reach across mounts; data needed on another mount must be copied there first. Backends implement `IFileSystemBackend` and return typed `FsResult<T>` (`Ok`/`Err`); besides plain disk-backed servers, `HaFileSystem` (Home Assistant entities/areas/actions), `ScheduleFileSystem` (scheduled tasks), and `PrinterQueueFileSystem` (print queue) are non-disk backends that follow the same contract. New filesystems are added by exposing a `filesystem://` resource from any MCP server — no agent changes needed.

Glob patterns support brace expansion (`GlobBraceExpander`): `**/*.{jpg,png}` expands to the union of `**/*.jpg` and `**/*.png` (lone/unbalanced `{...}` stay literal). All backends normalize glob entries to full virtual paths so results are consistent across mounts.

### Web Browsing Architecture

Web browsing runs in McpServerWebSearch via three tools: `web_browse` (navigate + extract content), `web_snapshot` (capture accessibility tree with interactive element refs), and `web_action` (interact with elements by ref — click, type, fill, select, etc.). The browser backend is `PlaywrightWebBrowser` connecting to Camoufox via WebSocket. `AccessibilitySnapshotService` injects JavaScript to traverse the DOM, infer ARIA roles, and assign unique refs (`e-1`, `e-2`, …) to interactive elements. `BrowserSessionManager` keeps pages alive per session with cookie persistence. `ModalDismisser` auto-closes common popups (cookie banners, newsletters, age gates).

### Camoufox

The `camoufox` Docker service provides an anti-detect browser (Firefox-based) for web scraping. McpServerWebSearch connects to it via WebSocket (`ws://camoufox:9377/browser`). Configuration is in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`).

**Bumping `Microsoft.Playwright` is a two-sided change.** Playwright's connect handshake demands an exact client/server minor match — a mismatch fails with HTTP 428 `Playwright version mismatch`, not a subtle bug. So `DockerCompose/camoufox/Dockerfile` must move in lockstep: both the `mcr.microsoft.com/playwright:vX.Y.0-noble` base and `playwright-core@X.Y.0`. Camoufox's bundled Firefox carries its own (older) juggler protocol, so a new playwright-core can also send fields it rejects; `patch-viewport.js` strips the `screenSize`/`isMobile` fields 1.61 added (upstream [camoufox#653](https://github.com/daijro/camoufox/issues/653) — their own Python library just pins `playwright<1.61`). Both `patch-*.js` scripts are anchor-checked and **exit 1 when their anchor disappears**, so a version bump fails the image build loudly instead of shipping a broken browser. Rebuild the image and run `Tests/Integration/Clients/` (Camoufox, ModalDismisser, PlaywrightWebBrowser, jQuery-widget) after any bump.

### Debugging with Playwright

When automating the WebChat with Playwright, use `ignoreHTTPSErrors: true` for the browser context when testing locally (the Let's Encrypt certificate is valid for `assistants.herfluffness.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, otherwise sends are silently rejected with a toast error.
