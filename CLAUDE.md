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
| `McpServer*` | MCP tool servers (Library, Vault, WebSearch, Idealista, Printer) |
| `McpChannel*` | MCP channel servers — each bridges a transport to the agent |
| `McpChannelSignalR` | WebChat/SignalR channel — hosts SignalR hub, streams, approvals, push notifications |
| `McpChannelTelegram` | Telegram channel — multi-bot polling (one bot per agent), inline keyboard approvals |
| `McpChannelServiceBus` | Azure Service Bus channel — queue processor, auto-approval |
| `McpChannelVoice` | Voice channel — Wyoming hub that dials hardware satellites, whisper STT, piper TTS, follow-up windows, announcements |
| `McpServerScheduling` | Scheduling server — dual-role: `filesystem://schedules` VFS + channel that fires due schedules as `channel/message` |
| `McpServerPrinter` | Printer server — `filesystem://print-queue` VFS that submits copied/created files to a configured IPP/CUPS printer |
| `WebChat`/`.Client` | Blazor WebAssembly chat interface, Redux-like state (Stores + Effects + HubEventDispatcher) |
| `Observability` | Metrics collector, REST API, SignalR hub — serves the Dashboard PWA |
| `Dashboard.Client` | Blazor WebAssembly observability dashboard (token costs, tool analytics, errors, schedules, memory, health) |
| `satellite` | `nabu-satellite` — standalone Rust crate (NOT in the .NET solution): fully static Wyoming satellite binary for Raspberry Pi with embedded wake-word detection |
| `Tests` | Unit and integration tests |

## Key File Locations

| What | Where |
|------|-------|
| Contracts | `Domain/Contracts/*.cs` |
| DTOs | `Domain/DTOs/*.cs` |
| Agent implementations | `Infrastructure/Agents/*.cs` |
| External clients | `Infrastructure/Clients/**/*.cs` |
| MCP tool server tools | `McpServer*/McpTools/*.cs` |
| Channel MCP tools | `McpChannel*/McpTools/*.cs` |
| Channel services | `McpChannel*/Services/*.cs` |
| Channel protocol DTOs | `Domain/DTOs/Channel/*.cs` |
| WebChat state | `WebChat.Client/State/**/*.cs` |
| Dashboard state | `Dashboard.Client/State/**/*.cs` |
| Dashboard pages | `Dashboard.Client/Pages/*.razor` |
| Metric event DTOs | `Domain/DTOs/Metrics/*.cs` |
| Metrics publisher | `Infrastructure/Metrics/*.cs` |
| Observability services | `Observability/Services/*.cs` |
| Observability API endpoints | `Observability/MetricsApiEndpoints.cs` |
| Metric dimension/metric enums | `Domain/DTOs/Metrics/Enums/*.cs` |
| Metrics query service | `Observability/Services/MetricsQueryService.cs` |
| Dashboard components | `Dashboard.Client/Components/*.razor` |
| Dashboard services | `Dashboard.Client/Services/*.cs` |
| Subagent tools & feature | `Domain/Tools/SubAgents/*.cs` |
| Subagent prompt | `Domain/Prompts/SubAgentPrompt.cs` |
| Subagent DTOs | `Domain/DTOs/SubAgent*.cs` |
| Memory services | `Infrastructure/Memory/*.cs` |
| Memory contracts | `Domain/Contracts/IMemory*.cs` |
| Memory tools & feature | `Domain/Tools/Memory/*.cs` |
| Memory prompts | `Domain/Prompts/MemoryPrompts.cs` |
| Memory DI module | `Agent/Modules/MemoryModule.cs` |
| Memory extraction queue | `Domain/Memory/*.cs` |
| Subagent DI module | `Agent/Modules/SubAgentModule.cs` |
| Filesystem tools & feature | `Domain/Tools/FileSystem/*.cs` |
| Filesystem contracts | `Domain/Contracts/IFileSystem*.cs`, `Domain/Contracts/IVirtualFileSystemRegistry.cs` |
| Filesystem DTOs | `Domain/DTOs/FileSystemMount.cs`, `Domain/DTOs/FileSystem/*.cs` |
| Virtual filesystem registry | `Infrastructure/Agents/VirtualFileSystemRegistry.cs` |
| MCP filesystem backend | `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`, `McpFileSystemDiscovery.cs` |
| Local filesystem client | `Infrastructure/Clients/LocalFileSystemClient.cs` |
| Filesystem MCP resources | `McpServer{Vault,Library,Sandbox,HomeAssistant,Printer}/McpResources/FileSystemResource.cs` |
| Glob brace expansion / regex | `Domain/Tools/FileSystem/GlobBraceExpander.cs`, `Domain/Tools/FileSystem/GlobRegex.cs` |
| Home Assistant VFS engine | `Domain/Tools/HomeAssistant/Vfs/*.cs` |
| Scheduling server (channel + filesystem) | `McpServerScheduling/**/*.cs` |
| Schedule VFS engine | `Domain/Tools/Scheduling/Vfs/*.cs` |
| Scheduling prompt | `Domain/Prompts/SchedulingPrompt.cs` |
| Schedule DTO | `Domain/DTOs/Schedule.cs` |
| Printer server (filesystem) | `McpServerPrinter/**/*.cs` |
| Print queue VFS engine | `Domain/Tools/Printing/Vfs/*.cs`, `Domain/Tools/Printing/*.cs` |
| Printing prompt | `Domain/Prompts/PrintingPrompt.cs` |
| Printing contracts & DTOs | `Domain/Contracts/IPrinterClient.cs`, `Domain/Contracts/IPrintSpool.cs`, `Domain/DTOs/Printing/*.cs` |
| IPP printer client & spool | `Infrastructure/Clients/Printer/*.cs`, `Infrastructure/Printing/PrintSpool.cs` |
| Agent catalog | `Domain/Agents/MutableAgentCatalog.cs`, `Domain/Contracts/IAgentCatalog.cs`, `Domain/DTOs/Channel/AgentCatalogEntry.cs` |
| Channel protocol serialization | `Domain/DTOs/Channel/ChannelProtocol.cs` |
| Web browsing tools | `Domain/Tools/Web/*.cs` |
| Web browsing prompt | `Domain/Prompts/WebBrowsingPrompt.cs` |
| Web browser contracts | `Domain/Contracts/IWebBrowser.cs` |
| Playwright browser client | `Infrastructure/Clients/Browser/*.cs` |
| Satellite CLI flags & defaults | `satellite/src/config.rs` |
| Satellite state machine | `satellite/src/satellite/state_machine.rs` |
| Satellite wake detector | `satellite/src/wake/detector.rs` |
| Satellite Wyoming codec | `satellite/src/wyoming/{codec,event}.rs` |
| Satellite audio (mic/playback/cues) | `satellite/src/audio/*.rs` |
| Satellite LED & button | `satellite/src/led.rs`, `satellite/src/gpio.rs` |
| Satellite build & deploy | `satellite/scripts/*.sh`, `satellite/deploy/nabu-satellite.service`, `scripts/provision-satellite-rs.sh`, `scripts/wsl-satellite.sh` |
| Unit & integration tests | `Tests/{Unit,Integration}/**/*Tests.cs` |
| E2E tests | `Tests/E2E/{Dashboard,WebChat}/*E2ETests.cs` |
| E2E fixtures | `Tests/E2E/Fixtures/*.cs` |

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

## TDD

Follow Red-Green-Refactor for all features and bug fixes. Write a failing test first, then implement. See `.claude/rules/tdd.md` for full workflow.

## Local Development

### Docker Compose files

| File                                                | Purpose |
|-----------------------------------------------------|---------|
| `DockerCompose/docker-compose.yml`                  | Main service definitions |
| `DockerCompose/docker-compose.override.windows.yml` | Windows user secrets mount (`%APPDATA%/Microsoft/UserSecrets`) |
| `DockerCompose/docker-compose.override.linux.yml`   | Linux user secrets mount (`$HOME/.microsoft/usersecrets`) |
| `DockerCompose/docker-compose.override.no-dri.yml`  | Strips the `/dev/dri` device from `plex`/`mcp-sandbox` on hosts without a DRI render node (e.g. NVIDIA-only WSL2) |

### Launching

Pick the override file matching your OS:

```bash
# Linux / WSL
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer wyoming-whisper wyoming-piper qbittorrent jackett redis caddy camoufox homeassistant

# Windows
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.windows.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer wyoming-whisper wyoming-piper qbittorrent jackett redis caddy camoufox homeassistant
```

The base compose maps `/dev/dri` into `plex`/`mcp-sandbox` for GPU hardware acceleration. Hosts **without** a DRI render node (NVIDIA-only WSL2 has `/dev/dxg` + the NVIDIA Container Toolkit, never `/dev/dri`) will fail with `error gathering device information while adding custom device "/dev/dri"`. On those hosts, append `-f DockerCompose/docker-compose.override.no-dri.yml` as the last `-f` to strip the device. (The VS Code `docker-debug-up` task already includes this override — debug never needs the GPU.)

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
- **Outbound**: `send_reply` tool (agent response → user), `request_approval` tool (tool approval flow), `create_conversation` tool (agent-initiated conversations), `register_agents` tool (agent publishes its catalog to the channel)

On connect and after every reconnect, the agent registers its agent catalog (an `AgentCatalogEntry` list) with each channel via `register_agents` (`ChannelConnectionHost`). Channels consume this single-source catalog instead of duplicated `Agents` config — SignalR broadcasts `OnAgentsUpdated`, so WebChat refreshes its agent list live.

A server can be **dual-role** — both a channel (in `ChannelEndpoints`) and a tool/filesystem server (in an agent's `mcpServerEndpoints`); `mcp-scheduling` is both. For dual-role servers the channel-protocol tools (`send_reply`, `request_approval`, `register_agents`) are hidden from the LLM.

New transports can be added by deploying a new channel MCP server — zero agent changes needed.

### Voice Satellite Architecture

Voice is an MCP channel server (`McpChannelVoice`, channelId `voice`, container `mcp-channel-voice`, port 6015) plus hardware satellites. The hub is the Wyoming-protocol **client**: `WyomingSatelliteHost` dials out to every satellite that has an `Address` in `VoiceSettings.Satellites` (`Satellites__<id>__Address`, e.g. `tcp://192.168.5.55:10800`) and reconnects forever; address-less satellites stay in the catalog as announce targets but are never dialed (announcements to them report offline). Pipeline: satellite wakes locally → streams mic `audio-chunk`s → `SatelliteSession`/`SilenceGate` segment the utterance → wyoming-whisper STT → transcript dispatched as `channel/message` → agent reply synthesized by wyoming-piper → streamed back as `audio-start`/`audio-chunk`/`audio-stop`. Sending a `transcript` event to the satellite ends its turn and re-arms wake; `FollowUpConversation` can reopen the mic wake-free, announced by the `ListeningChime` earcon.

The satellite is `nabu-satellite` (`satellite/` — a standalone Rust crate, not part of the .NET solution): a fully static aarch64-musl binary (~18.8 MiB) embedding the openWakeWord "ok nabu" pipeline (melspectrogram → embedding → classifier ONNX, run in-process via tract) and the cue WAVs. Key invariants:

- **The satellite is the Wyoming SERVER; the hub dials in** (default `--listen 0.0.0.0:10700`). A new hub connection supersedes the previous one (abort + await) so a dead-peer TCP wedge can't hold the exclusive `plughw` mic for the ~15-min retransmission timeout. The three ONNX models are parsed + optimized ONCE at boot (`WakeModels::load`, fail-fast) and shared across connections — re-arm after a reconnect is instant.
- **Cancellation safety**: hub/mic reads AND playback writes/drains are multi-await compound I/O, NOT `select!`-safe; they run in dedicated pump tasks (hub, mic, playback) feeding bounded mpsc channels, and the main `select!` only races `recv()` futures.
- **Playback pump**: the pump task is the single owner of the playback device. `audio-stop`'s drain (~0.5-2 s of buffered TTS) happens inside the pump, so wake/button/mic stay live during the reply tail; drain completions return on an unbounded channel (bounded would AB-deadlock) carrying a generation that gates the LED Idle/Listening transition (a stale completion can't blank a newer stream); playback errors stay connection-fatal; cues route through the pump too, so a cue player can never EBUSY-race a reply for the exclusive device (cues are dropped while a stream is active).
- **Audio contract**: mic = 16 kHz mono S16LE in 1280-sample/80 ms chunks (arecord subprocess; bytes end-to-end internally, decoded to i16 only at the detector); playback sink = FIXED 22 050 Hz mono S16LE (aplay) that ignores announced rates — everything it plays must be 22 050 Hz: hub-side TTS and chime (this is why `ListeningChime` generates 22 050 Hz PCM) plus the satellite's own embedded cue WAVs.
- **ALSA latency flags**: the default commands carry `arecord … -F 20000` (20 ms periods; the alsa-utils default of buffer/4 = 125 ms delayed every mic sample on the wake and STT paths) and `aplay … --start-delay=100000 -F 50000` (start at ~100 ms queued instead of the full-500 ms-buffer default; buffer stays 500 ms for underrun headroom). Keep them when overriding devices. Plain-argv audio commands exec directly (no `sh -c`), so kill/supersede SIGKILLs aplay/arecord themselves; shell-shaped commands (WSL gain pipe) still go through sh.
- **Zero-lag pre-roll**: while idle, mic chunks fill a pre-roll ring (`--preroll-ms`, default 1000); a wake trigger flushes only the detection gap (3 chunks ≈ 240 ms), never the wake word itself; a button press flushes the full ring.
- **LED**: the state machine publishes `LedState` (Idle/Listening/Thinking/Speaking) on a tokio watch channel; a per-connection render task owns the backend (`--led-gpio` pin or `--led-spi` for the ReSpeaker HAT APA102s on `/dev/spidev0.1`); Idle→off, everything else→steady on, 120 s Thinking fallback mirroring the hub reply timeout; missing/failing LED hardware is never fatal. Idle after a reply still means actual-playback-complete (drain-completion-driven).
- **Defaults target a Jabra Speak2** on `plughw:0,0` (index-pinned via `snd_usb_audio index=0` because the ALSA card name varies by variant: 75→J75, 55 MS→MS, 55 UC→UC), no button/LED — the Jabra's buttons/LEDs are HID-telephony, unusable on Linux. The ReSpeaker 2-Mic HAT is the override path: `plughw:CARD=seeed2micvoicec,DEV=0` on both audio commands plus `--button-gpio 17 --led-spi` (needs `dtparam=spi=on` and the `spi` group).
- **Wire format**: frames are encoded as one contiguous buffer with event `data` sent once as the `data_length` body (the hub's reader prefers the body; its writer emits the same shape) — pinned by a codec test.

Build & deploy: `satellite/scripts/build-release.sh` cross-compiles via cargo-zigbuild + zig (the `zigcc-fp16-shim.sh` CC shim rewrites tract-linalg's `+fp16` -march feature to zig's `+fullfp16`) — never run bare `cargo zigbuild` for releases. `satellite/.cargo/config.toml` pins `-C target-cpu=cortex-a53 -C target-feature=-aes,-sha2` for the musl target (the Pi's silicon lacks the crypto extensions LLVM's cortex-a53 def would enable). `scripts/provision-satellite-rs.sh <user@host> [mic-device]` installs the binary plus the templated `satellite/deploy/nabu-satellite.service` unit on a Pi (only dependency: `alsa-utils`; the unit pins the `performance` governor and `Nice=-10`) and, when the mic device is left at the default `plughw:0,0`, applies the Jabra ALSA/udev pinning. qemu-emulation smoke tests need `--no-wake` (qemu's fp16 hwcaps activate tract f16 kernels that crash under emulation; a real A53 selects the f32 kernels). On-device E2E validation (plan task 5.3) is still open, blocked on hardware — it should also read the `RUST_LOG=debug` per-chunk "wake inference" timing line.

### Running a Satellite on WSL

`scripts/wsl-satellite.sh` builds (`cargo build --release`, native target) and runs a satellite on the WSL host through WSLg PulseAudio; the dockerized hub dials `tcp://host.docker.internal:$SAT_PORT` (defaults match `McpChannelVoice/appsettings.Development.json`: 10700 = fran-office-01, 10600 = laura-office-01). Env knobs: `SAT_PORT`, `THRESHOLD` (wake threshold, default 0.5), `MIC_GAIN` (default 3.0 via a python `audioop` pipe — WSLg's mic bridge is quiet and the binary has no gain flag; needs python ≤ 3.12), `RUST_LOG`. `paplay --latency-msec=50` is mandatory on WSLg (the RDP sink's default buffer adds ~1.6 s playback latency, so the hub opens the wake-free follow-up window — 400 ms playback-tail echo guard, then 7 s window — while the reply is still audibly playing). Stale instances are detected via an `ss` LISTEN-state check because WSL2 mirrored networking makes loopback connects to dead ports hang instead of refusing.

**WSLg's RDP audio bridge audibly degrades playback** (harsh/crackly; the Linux-side chain measures bit-clean at the Pulse monitor tap — the corruption is in the RDPSink→Windows leg). `scripts/wsl-satellite-winaudio.sh` is the clean-audio variant: same satellite in WSL, but the audio commands are Windows binaries run through WSL interop — `ffmpeg.exe` dshow mic capture (`-audio_buffer_size 50`, the dshow analogue of arecord's `-F 20000`) and `ffplay.exe` WASAPI playback (`-af adelay=150:all=1` because every fresh playback session can randomly glitch its first instants; a 120-180 ms earcon lives entirely inside that window, so the pad moves the artifact into silence). Needs the gyan.dev ffmpeg-release-essentials zip extracted to `%LOCALAPPDATA%\nabu-satellite\`. The dockerized hub only dials the dev satellite addresses when running with `ASPNETCORE_ENVIRONMENT=Development` (its `appsettings.Development.json` overrides exactly the `Satellites` addresses; production config points at the Pi IPs).

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

The agent exposes a unified virtual filesystem across MCP servers. Each MCP server can expose a `filesystem://` resource (e.g., `filesystem://vault`, `filesystem://media`, `filesystem://ha`, `filesystem://schedules`, `filesystem://print-queue`). At session start, `McpFileSystemDiscovery` detects these resources and mounts them into a `VirtualFileSystemRegistry` with longest-prefix path resolution. `FileSystemToolFeature` provides 8 domain tools (`VfsTextRead`, `VfsTextCreate`, `VfsTextEdit`, `VfsGlobFiles`, `VfsTextSearch`, `VfsMove`, `VfsRemove`, `VfsExec`) that dispatch through the registry. `VfsExec` is filesystem-conditional — backends that don't implement `fs_exec` return a "tool missing" envelope when invoked. Raw MCP `fs_*` tools are filtered out when domain tools are active. Backends implement `IFileSystemBackend` and return typed `FsResult<T>` (`Ok`/`Err`); besides plain disk-backed servers, `HaFileSystem` (Home Assistant entities/areas/actions), `ScheduleFileSystem` (scheduled tasks), and `PrinterQueueFileSystem` (print queue) are non-disk backends that follow the same contract. New filesystems are added by exposing a `filesystem://` resource from any MCP server — no agent changes needed.

Glob patterns support brace expansion (`GlobBraceExpander`): `**/*.{jpg,png}` expands to the union of `**/*.jpg` and `**/*.png` (lone/unbalanced `{...}` stay literal). All backends normalize glob entries to full virtual paths so results are consistent across mounts.

### Web Browsing Architecture

Web browsing runs in McpServerWebSearch via three tools: `web_browse` (navigate + extract content), `web_snapshot` (capture accessibility tree with interactive element refs), and `web_action` (interact with elements by ref — click, type, fill, select, etc.). The browser backend is `PlaywrightWebBrowser` connecting to Camoufox via WebSocket. `AccessibilitySnapshotService` injects JavaScript to traverse the DOM, infer ARIA roles, and assign unique refs (`e-1`, `e-2`, …) to interactive elements. `BrowserSessionManager` keeps pages alive per session with cookie persistence. `ModalDismisser` auto-closes common popups (cookie banners, newsletters, age gates).

### Camoufox

The `camoufox` Docker service provides an anti-detect browser (Firefox-based) for web scraping. McpServerWebSearch connects to it via WebSocket (`ws://camoufox:9377/browser`). Configuration is in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`).

### Debugging with Playwright

When automating the WebChat with Playwright, use `ignoreHTTPSErrors: true` for the browser context when testing locally (the Let's Encrypt certificate is valid for `assistants.herfluffness.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, otherwise sends are silently rejected with a toast error.