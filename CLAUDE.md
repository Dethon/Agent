# Agent

AI agent via Telegram/WebChat/MessageBus using .NET 10 LTS, MCP, and OpenRouter LLMs.

## Verify Before Assuming

Before proposing any architectural change or debugging hypothesis, verify your assumptions against the actual state (read the file, run the command, check the config). Never assume something is missing or broken without evidence.

## Projects

| Project | Purpose |
|---------|---------|
| `Agent` | Composition root, DI, connects to channel and tool MCP servers |
| `Domain` | Contracts, DTOs, business logic |
| `Infrastructure` | External clients, agent implementations, push notifications |
| `McpServer*` | MCP tool servers (Library, Vault, Sandbox, WebSearch, Idealista, HomeAssistant, Printer, Scheduling, Timers) |
| `McpChannel*` | MCP channel servers, one per transport: `SignalR` (WebChat hub, streams, approvals, push), `Telegram` (one bot per agent, inline-keyboard approvals), `ServiceBus` (queue processor, auto-approval), `Voice` (Wyoming hub → hardware satellites) |
| `WebChat`/`.Client` | Blazor WebAssembly chat interface, Redux-like state (Stores + Effects + HubEventDispatcher) |
| `Observability` | Metrics collector, REST API, SignalR hub — serves the Dashboard PWA |
| `Dashboard.Client` | Blazor WebAssembly observability dashboard (token costs, tool analytics, errors, latency, voice, schedules, memory, health) |
| `satellite` | `nabu-satellite` — standalone Rust crate (NOT in the .NET solution); see `satellite/CLAUDE.md` |
| `Tests` | Unit, integration, and E2E tests |

## Key File Locations

| What | Where |
|------|-------|
| Contracts / DTOs | `Domain/Contracts/*.cs`, `Domain/DTOs/*.cs` |
| Agent implementations / external clients | `Infrastructure/Agents/*.cs`, `Infrastructure/Clients/**/*.cs` |
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
| Filesystem MCP resources | `McpServer{Vault,Library,Sandbox,HomeAssistant,Printer,Scheduling,Timers}/McpResources/FileSystemResource.cs` |
| Home Assistant VFS engine | `Domain/Tools/HomeAssistant/Vfs/*.cs` |
| Scheduling | `McpServerScheduling/**/*.cs`, engine `Domain/Tools/Scheduling/Vfs/*.cs`, `Domain/Prompts/SchedulingPrompt.cs`, `Domain/DTOs/Schedule.cs` |
| Printing | `McpServerPrinter/**/*.cs`, engine `Domain/Tools/Printing/{,Vfs/}*.cs`, `Domain/Prompts/PrintingPrompt.cs`, `Domain/{Contracts/IPrinter*,Contracts/IPrintSpool,DTOs/Printing/*}.cs`, `Infrastructure/{Clients/Printer,Printing}/*.cs` |
| Timers | `McpServerTimers/**/*.cs`, engine `Domain/Tools/Timers/Vfs/*.cs`, `Domain/Prompts/TimerPrompt.cs`, contracts `Domain/Contracts/{IInsistentAnnouncer,IAlertDismisser,ISatelliteCatalog}.cs` |
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

New configuration lives in `appsettings.json` / `appsettings.Development.json` by default. `DockerCompose/docker-compose.yml`'s `environment` block and `DockerCompose/.env` are not a mirror of every setting — they exist only for:

- **Secrets** (API keys, connection strings, credentials) — placeholder entry in `DockerCompose/.env` (never a real value), wired into `docker-compose.yml` as `${VAR_NAME}`.
- **Non-generic parameters** — inherently per-deployment values (a satellite's host IP, a topology-dependent URL) — a `docker-compose.yml` environment entry (placeholder like `changeme` where there's no safe default).

A new generic tunable (threshold, window, feature flag) belongs in `appsettings.json` **alone**. When adding code that reads a new setting, update whichever category applies in the same change.

## OpenRouter Provider Routing

Each `agents[]` / `subAgents[]` entry may carry a `providerRouting` object (`sort` ∈
`price`|`throughput`|`latency`, plus `order`, `only`, `ignore`, `allowFallbacks`), overriding
`openRouter.providerRouting` **wholesale** — never field-by-field. It reaches the wire through
the same path as `session_id`: `MultiAgentFactory.ResolveRouting` → `OpenRouterChatClient` →
`ReasoningHandler` → `OpenRouterHttpHelpers.PrepareRequestBodyAsync`, which stamps `provider`.
`{}` is not the wholesale opt-out it looks like: the JSON config provider records an empty
object as a null-valued key, `Get<ProviderRouting>()` returns null for it, and `declared ??
global` inherits the global — opting an agent back to balanced routing under a non-empty global
default needs a value-bearing field that doesn't change routing: `{"allowFallbacks": true}`
binds to a real object that shadows the global wholesale while leaving `sort`/`order` unset,
and `allow_fallbacks: true` is OpenRouter's default anyway (pinned by
`ProviderRoutingBindingTests`).

**Balanced routing is the absence of the object.** OpenRouter has no `sort` value for its
default load balancing (uptime filter, then inverse-square price weighting) — it is only
reachable by sending neither `sort` nor `order`, so the global default ships unset and
`AgentAppSettingsTests` pins it that way. `sort: "price"` is a different thing: deterministically
the cheapest provider, not a weighted spread.

**`order` costs the prompt cache.** Sticky routing — the reason every request carries a
`session_id` — is disabled when `provider.order` is set, so the ~17k-token static prefix is
re-sent uncached every turn. `sort` does *not* disable it. Prefer `only` + `sort` to restrict
the provider set. `ProviderRoutingAdvisories` logs a warning for this and for a `:nitro`/`:floor`
model suffix fighting an explicit `sort`; both are warnings, never throws, because the same path
serves runtime-created agents. The advisories run at agent/subagent construction
(`MultiAgentFactory.ResolveRouting`) with no dedupe — agents are constructed per conversation
activation and subagents per spawn, so a tripped advisory repeats for the lifetime of the config,
not once per agent. `MemoryModule` binds `openRouter.providerRouting` for the memory extraction
and dreaming chat clients directly, so those two models skip the advisories entirely.

Current: `nabu` latency, `jonas-worker` throughput, everything else balanced.

## Multi-Agent Patterns

- **Stuck workers**: replace, don't wait — spawn a fresh agent for the same task. Never retry the same failing action more than twice; after two failures reassess or escalate.
- **Layer completion**: check `TaskList` for `completed` on every task in a layer before starting dependent work or reporting success. Never infer completion from partial signals.
- **Auto-commit** after each TDD triplet (RED → GREEN → REVIEW) succeeds, with a message referencing the triplet's feature.

## Local Development

### Docker Compose files

| File                                                | Purpose |
|-----------------------------------------------------|---------|
| `DockerCompose/docker-compose.yml`                  | Main service definitions |
| `DockerCompose/docker-compose.override.windows.yml` | Windows user secrets mount (`%APPDATA%/Microsoft/UserSecrets`) |
| `DockerCompose/docker-compose.override.linux.yml`   | Linux user secrets mount (`$HOME/.microsoft/usersecrets`) |
| `DockerCompose/docker-compose.override.no-dri.yml`  | Strips `/dev/dri` from `plex`/`mcp-sandbox`/`lemonade` (and forces `lemonade` STT to CPU) on hosts without a DRI render node |

### Launching

Swap the override for your OS (`linux` on Linux/WSL, `windows` on Windows):

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build \
  agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library \
  mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer mcp-timers \
  lemonade tse-extractor qbittorrent jackett redis caddy camoufox homeassistant music-assistant
```

The base compose maps `/dev/dri` into `plex`/`mcp-sandbox`/`lemonade` for GPU acceleration. Only the render node is mapped — the Vulkan tier needs nothing more, so `/dev/dri` without `/dev/kfd` (Intel iGPU, Raspberry Pi) still comes up; `/dev/kfd` (ROCm) is never mapped. The opt-in NPU tier (`docker-compose.override.npu.yml`) maps `/dev/accel/accel0` instead. Hosts with **no** DRI render node (NVIDIA-only WSL2 has `/dev/dxg`, never `/dev/dri`) fail with `error gathering device information while adding custom device "/dev/dri"` — append `-f DockerCompose/docker-compose.override.no-dri.yml` last to strip it (the VS Code `docker-debug-up` task already does).

### Secrets

Services read secrets from .NET User Secrets mounted at `/home/app/.microsoft/usersecrets`; the OS override files map the host-side path. A crash with `Value cannot be an empty string. (Parameter 'connectionString')` means they aren't mounted — check you're using the right override.

### Accessing the WebChat & Dashboard

Caddy (port 443, Let's Encrypt TLS) is the entry point: `/hubs/*` → McpChannelSignalR, `/dashboard/*` → Observability, everything else → WebUI. **Connect through Caddy, not directly to webui:5001**, or SignalR won't reach the channel server.

The dashboard (an installable PWA) is at `https://assistants.herfluffness.com/dashboard/` or `http://localhost:5003/dashboard/` direct.

### Accessing Home Assistant

Home Assistant runs at `http://<host>:8123` (published on all interfaces). On first run:

1. Create the owner account through the browser onboarding flow.
2. Profile menu → **Security → Long-Lived Access Tokens** → create one.
3. Set `HOMEASSISTANT__TOKEN=...` in `DockerCompose/.env` and restart `mcp-homeassistant`.
4. For the Roborock S8: Settings → Devices & Services → Add Integration → **Roborock**; the vacuum appears as `vacuum.<name>`.

The agent reaches HA in-network at `http://homeassistant:8123` via `McpServerHomeAssistant`. For voice alarms/reminders it creates events on a dedicated `calendar.assistant_alarms` calendar that an HA automation bridges to the voice announce endpoint; the `home_assistant_guide` prompt (`Domain/Prompts/HomeAssistantPrompt.cs`) teaches the idiom, and the one-time `rest_command` + automation provisioning lives in the HA instance itself.

### Observability Architecture

Services publish `MetricEvent` DTOs via `IMetricsPublisher` → Redis Pub/Sub channel `metrics:events`. `MetricsCollectorService` subscribes, aggregates into Redis (sorted sets for time-series, hashes for totals, TTL keys for health), and forwards live events to the SignalR hub (`/hubs/metrics`); `MetricsQueryService` serves grouped aggregations by dimension/metric enum. The dashboard is hybrid: REST for history on page load, SignalR for live updates, `LocalStorageService` for UI state.

Health tiles come from `ServiceHealthRegistry`, a sorted-set roster (`metrics:health:seen`) scored by *last registration*, not last health — reachability is the separate TTL'd `metrics:health:<service>` key. Services publishing `HeartbeatEvent`s register themselves; third-party containers are registered by `HttpHealthProbeService`, which polls the URLs in `HttpProbes` (`Observability/appsettings.json`) and treats **any** HTTP response, even non-2xx, as up. A probe target re-registers every cycle whether or not it answers, so a down service stays visible as a red tile, while a retired one stops registering and ages off after `Retention` (7 days).

### Memory Architecture

Built into the Agent process, not an MCP server:
- **Extraction** — `ChatMonitor` queues turns → `MemoryExtractionWorker` fetches the persisted thread and slices a window anchored at the recall point → `IMemoryExtractor` (LLM) reads it (rendered by `ConversationWindowRenderer` with `[CURRENT]`/`[context -N]` markers) → `IMemoryStore` (Redis Stack, vector search) persists. Falls back to raw message content when the thread is unavailable.
- **Recall** — `MemoryRecallHook` runs before each turn: builds a user-only window, sets the extraction anchor, semantic-searches, injects into the system prompt.
- **Dreaming** — `MemoryDreamingService` periodically consolidates/prunes via `IMemoryConsolidator` (LLM).
- All three publish `MetricEvent`s.

### Channel Architecture

Transports (WebChat, Telegram, ServiceBus, Voice, Scheduling) are independent MCP channel servers; the agent connects as an MCP client via `ChannelEndpoints`. Wire serialization is centralized in `ChannelProtocol` (shared `JsonSerializerOptions` + typed records — `ChannelMessageNotification`, `ChannelCancelNotification`, `RegisterAgentsParams`, `RequestApprovalParams`). Inbound: `channel/message`, `channel/cancel`. Outbound tools: `send_reply`, `request_approval`, `create_conversation`, `register_agents`. A new transport needs only a new channel server — zero agent changes.

- `create_conversation` doubles as the turn-start announce when given `existingConversationId`: ChatMonitor calls it channel-agnostically for agent-initiated messages (`Origin` set) into existing conversations, and each channel applies its own semantics — SignalR opens a live stream + `OnStreamChanged(Started)` before reply chunks arrive; voice no-ops on a live satellite session, else binds the turn as an announcement.
- On connect and every reconnect the agent registers its `AgentCatalogEntry` list via `register_agents` (`ChannelConnectionHost`); channels use this single source instead of duplicated `Agents` config, and SignalR broadcasts `OnAgentsUpdated` so WebChat refreshes live.
- `attachOnly: true` in `ChannelEndpoints` (voice) makes `DeliveryTargetResolver` order that channel last — it attaches to conversations minted elsewhere, never mints the primary target.
- **Dual-role** servers are both a channel and a tool/filesystem server (`mcp-scheduling`, `mcp-library` download alerts); their channel-protocol tools are hidden from the LLM.

### Voice Satellite Architecture

Voice is an MCP channel server (`McpChannelVoice`, channelId `voice`, container `mcp-channel-voice`, port 6015) plus hardware satellites. The hub is the Wyoming **client**: `WyomingSatelliteHost` dials every satellite with an `Address` in `VoiceSettings.Satellites` (`Satellites__<id>__Address`, e.g. `tcp://192.168.5.55:10800`) and reconnects forever; address-less satellites stay in the catalog as announce targets but are never dialed (announcements report offline).

Pipeline: satellite wakes locally → streams mic `audio-chunk`s → `SatelliteSession`/`SilenceGate` segment the utterance → **speaker-verification gate** (`Services/Verification`, ONNX embeddings scored against profiles enrolled under `/voices` via `scripts/enroll-voice.sh`; non-enrolled audio is dropped pre-STT, a conclusive match routes the speaker's folder name into the message sender for per-person memory) → optional **TSE** (`Services/Tse`, an STT decorator calling the `tse-extractor` container; `Tse__Mode` ∈ Off|Auto|Always, Auto gated by `NoiseFloorThreshold`) → **Lemonade STT** (OpenAI `/v1/audio/transcriptions`, Whisper-Medium on whisper.cpp; `STT_BACKEND` ∈ cpu|gpu, or the experimental NPU tier via `docker-compose.override.npu.yml` + `STT_MODEL`; decode quality via `STT_VAD_THRESHOLD`/`STT_INITIAL_PROMPT`/`STT_BEAM_SIZE` — defaults Silero VAD 0.6 + Castilian initial prompt + beam 5, empty disables, NPU/flm ignores them) → transcript dispatched as `channel/message` → reply spoken as it streams, segment-by-segment with prefetch, via **Lemonade Kokoro** (`/v1/audio/speech`, 24 kHz PCM resampled in-hub to 22 050 Hz) → back as `audio-start`/`audio-chunk`/`audio-stop`.

**Alert routing.** Insistent announces — timers and alarms, i.e. exactly the `/api/voice/announce`
requests carrying `insistent` — are marked `alert: true` on the Wyoming `audio-start` (protocol
1.5, `WyomingSatelliteHost.BuildAudioStart`; `InsistentAnnouncementController` is the only producer,
via `PlaybackJob.Alert`). The satellite plays a marked stream on `--alert-snd-command` instead of
`--snd-command`: on music units a non-attenuated `alert` ALSA softvol, so an alert is not capped by
the calibrated conversational `TTS` level. `AnnouncePriority.High` is deliberately not the marker —
approval prompts share it. The flag defaults to false everywhere, so ordinary replies, plain
announcements and a pre-1.5 satellite are unaffected, and an unopenable alert device falls back to
the normal sink rather than dropping the connection. The satellite's level chain is three per-source
softvols (`Music`, `TTS`, `Alert`) under a PipeWire master held at 100 %; see
`scripts/provision-satellite-rs.sh` for `TTS_VOLUME` / `ALERT_VOLUME`.

Sending a `transcript` event ends the satellite's turn and re-arms wake; `FollowUpConversation` reopens the mic wake-free, announced by the `ListeningChime` earcon and, on the wire, by a `listening-started` event (protocol 1.6) that returns the satellite's LED from Thinking to Listening — it cannot infer the moment itself, because its capture never closed. When several satellites hear the same wake word, `WakeArbiter` picks one winner (calibrated `wake_rms`, 500 ms coincidence window, onset-alignment check against open captures) and silently re-arms the losers via `pause-satellite`; a much-louder wake during another satellite's open conversation hands the conversation over.

**The satellite side is `satellite/CLAUDE.md` — read it before touching either side of the wire.** What the hub must respect: the satellite is the Wyoming **server** (the hub dials in), and its playback sink is FIXED 22 050 Hz mono S16LE regardless of announced rates, so all hub-emitted audio (TTS, `ListeningChime`) must be 22 050 Hz. The dockerized hub dials the dev satellite addresses only under `ASPNETCORE_ENVIRONMENT=Development` (`McpChannelVoice/appsettings.Development.json` overrides exactly the `Satellites` addresses; production points at the Pi IPs).

### Scheduling Architecture

`McpServerScheduling` is a dual-role MCP server:
- **`filesystem://schedules` resource** (mount `/schedules`) — managed with the standard `domain__filesystem__*` tools. Layout: `/schedules/<agentId>/<scheduleId>/schedule.json` (`{prompt, cron|runAt, userId?, deliverTo?}` — exactly one of recurring `cron` or one-shot `runAt`), plus `agent_info.json` and read-only `status.json` (`createdAt`/`lastRunAt`/`nextRunAt`). `fs_exec run_now.sh` on a schedule directory fires it immediately. The `ScheduleFileSystem` engine (`Domain/Tools/Scheduling/Vfs/`) implements `IFileSystemBackend`, returning typed `FsResult<T>`.
- **Channel** — `ScheduleDispatcherService` polls `IScheduleStore` for due schedules, `ScheduleFirePlanner` chooses delete-after-fire (one-shot) vs. update-next-run (cron), and `ScheduleNotificationEmitter` emits `channel/message`. The agent runs the prompt; `ChatMonitor` fans the result out to `deliverTo`, minting conversations as needed.

The `scheduling_prompt` (`Domain/Prompts/SchedulingPrompt.cs`) teaches the `/schedules` idiom.

### Printing Architecture

`McpServerPrinter` is a non-disk MCP filesystem server exposing **`filesystem://print-queue`** (mount `/print-queue`) backed by `PrinterQueueFileSystem` (`Domain/Tools/Printing/Vfs/`). Copying or creating a file into `/print-queue/<filename>` (bytes via `fs_blob_write` chunk streaming) immediately submits it to the single configured printer; `fs_delete` on an active job cancels it; `move` and `exec` are unsupported. Two contracts back it:
- **`IPrinterClient`** — `IppPrinterClient` (`Infrastructure/Clients/Printer/`), a `SharpIppNext` + `HttpClient` adapter against `PRINTERURI`, mapping `Print-Job`/`Get-Jobs`/`Cancel-Job`. `IppJobStateMapper` maps IPP states to `PrintJobState`. Get-Jobs requests `job-state` so active jobs aren't pruned mid-print, and `GetActiveJobsAsync` defensively drops non-active states for printers that ignore `WhichJobs.NotCompleted`.
- **`IPrintSpool`** — `PrintSpool` (`Infrastructure/Printing/`), disk-backed under `/spool`, keyed by filename, holding `{JobId, ContentType, Bytes, SubmittedAt, MissingSince}` so `read`/`search`/`edit`/blob read-back work while a job is active (blobs use the `.blob` suffix). `PrintQueueCoordinator` prunes during reconciliation but **debounces absence**: a job is dropped only after staying absent from the printer's active set past `ReconcileGraceMilliseconds`, so a just-submitted job (or a transient empty `Get-Jobs`) isn't lost mid-print.

Accepted formats are configurable via **`SupportedFormats`** (default `text,jpeg,pwg-raster,urf,pcl`); anything else is rejected on copy-in. It is the single source of truth — the `printing_prompt` (`PrintingPrompt.Build`/`DescribeFormats`) and the resource description derive their advertised list from it, so accepted and advertised can't drift. Submission is `application/octet-stream` (IPP printers reject unknown content types); text is CRLF-normalized (content-sniffed for octet-stream copies) to stop staircase printing, images use `print-scaling=fit`, and `PrintableContent` (`Domain/Tools/Printing/`) does detection/normalization. `/print-queue/status.json` is a read-only view; finished jobs disappear from the listing.

### Timers Architecture

Countdown timers live in **`McpServerTimers`** (container `mcp-timers`, port 6016), a **pure filesystem tool server** (McpServerPrinter shape) exposing `filesystem://timers` (mount `/timers`). Agents mount it via `mcpServerEndpoints` — it is **not** a channel, because a timer just *rings*, it doesn't message the agent. `TimerFileSystem` (`Domain/Tools/Timers/Vfs/`), the non-durable `InMemoryTimerStore` and `TimerFireService` (polls for due timers) all run here.

The three things a timer needs that only exist in the voice hub go over HTTP, so the hub stays the single source of truth for ringing and satellite resolution:
- **Fire** — `TimerFireService` POSTs to `/api/voice/announce` (`HttpInsistentAnnouncer`); ringing runs on the hub's live Wyoming sessions.
- **Dismiss** — `exec dismiss.sh` POSTs to `/api/voice/dismiss` (`HttpAlertDismisser`), cancelling the hub's live `ActiveAlertRegistry` CTSs. Wake/button dismissal stays hub-local.
- **Validate target** — `CreateAsync` resolves via `GET /api/voice/satellites` (roster, fetched fresh per call — it only changes on hub restart) + `POST /api/voice/satellites/resolve` (`HttpSatelliteCatalog`). Resolution is **never** done in the timers process: the hub's `SatelliteRegistry` dual-keys rooms on `Room` and `DisplayLocation`, so forwarding keeps create-time validation byte-identical to fire-time routing.

All three endpoints share `VoiceHubAuth` (token via `X-Announce-Token`/`Announce__Token`; `BindToLoopbackOnly` must stay false so the separate container can reach them); `HttpClient.BaseAddress` comes from `VoiceHub__BaseUrl`. `IInsistentAnnouncer`, `IAlertDismisser` and `ISatelliteCatalog` (`Domain/Contracts`) are async precisely so the same engine code runs unchanged in-process or over HTTP. The `timers_prompt` (`Domain/Prompts/TimerPrompt.cs`) embeds the live roster at prompt-fetch time (`TimersSystemPrompt` asks `ISatelliteCatalog` with a 2 s cap and fails open to roster-less static text, so an unreachable hub can never stall or fail session build) and tells the LLM to branch on channel: on a voice turn default the target to the speaking room; elsewhere offer the roster rooms and ask.

### Virtual Filesystem Architecture

Each MCP server can expose a `filesystem://` resource (`vault`, `media`, `ha`, `schedules`, `print-queue`, `timers`). At session start `McpFileSystemDiscovery` detects them and mounts them into `VirtualFileSystemRegistry` with longest-prefix path resolution. `FileSystemToolFeature` provides 10 domain tools (`VfsTextRead`, `VfsTextCreate`, `VfsTextEdit`, `VfsGlobFiles`, `VfsTextSearch`, `VfsMove`, `VfsCopy`, `VfsRemove`, `VfsExec`, `VfsFileInfo`) dispatching through the registry; raw MCP `fs_*` tools are filtered out while domain tools are active. `VfsExec` is filesystem-conditional — backends without `fs_exec` return a "tool missing" envelope.

Each mount is its own backend — **tools cannot reach across mounts**; data needed elsewhere must be copied there first. Backends implement `IFileSystemBackend` returning typed `FsResult<T>` (`Ok`/`Err`); besides disk-backed servers, `HaFileSystem`, `ScheduleFileSystem`, `PrinterQueueFileSystem` and `TimerFileSystem` are non-disk backends on the same contract. New filesystems need no agent changes.

Globs support brace expansion (`GlobBraceExpander`): `**/*.{jpg,png}` = union of both patterns (lone/unbalanced `{...}` stay literal). All backends normalize glob entries to full virtual paths.

### Web Browsing Architecture

McpServerWebSearch exposes `web_browse` (navigate + extract), `web_snapshot` (accessibility tree with interactive element refs) and `web_action` (click/type/fill/select by ref). The backend is `PlaywrightWebBrowser` over a WebSocket to Camoufox. `AccessibilitySnapshotService` injects JS to traverse the DOM, infer ARIA roles and assign refs (`e-1`, `e-2`, …); `BrowserSessionManager` keeps pages alive per session with cookie persistence; `ModalDismisser` auto-closes cookie banners, newsletters and age gates.

### Camoufox

The `camoufox` service is an anti-detect Firefox for scraping, reached at `ws://camoufox:9377/browser`; config in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`).

**Bumping `Microsoft.Playwright` is a two-sided change.** The connect handshake demands an exact client/server minor match — a mismatch fails hard with HTTP 428 `Playwright version mismatch`. `DockerCompose/camoufox/Dockerfile` must move in lockstep: both the `mcr.microsoft.com/playwright:vX.Y.0-noble` base and `playwright-core@X.Y.0`. Camoufox's bundled Firefox carries an older juggler protocol, so a newer playwright-core can send fields it rejects; `patch-viewport.js` strips the `screenSize`/`isMobile` fields 1.61 added (upstream [camoufox#653](https://github.com/daijro/camoufox/issues/653) — their own Python library just pins `playwright<1.61`). Both `patch-*.js` scripts are anchor-checked and **exit 1 when their anchor disappears**, so a bump fails the image build loudly instead of shipping a broken browser. Rebuild the image and run `Tests/Integration/Clients/` after any bump.

### Debugging with Playwright

Use `ignoreHTTPSErrors: true` for the browser context locally (the certificate is valid for `assistants.herfluffness.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, or sends are silently rejected with a toast error.
