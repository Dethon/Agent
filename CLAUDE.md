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
| `McpServer*` | MCP tool servers (Library, Vault, WebSearch, Idealista) |
| `McpChannel*` | MCP channel servers — each bridges a transport to the agent |
| `McpChannelSignalR` | WebChat/SignalR channel — hosts SignalR hub, streams, approvals, push notifications |
| `McpChannelTelegram` | Telegram channel — multi-bot polling (one bot per agent), inline keyboard approvals |
| `McpChannelServiceBus` | Azure Service Bus channel — queue processor, auto-approval |
| `WebChat`/`.Client` | Blazor WebAssembly chat interface, Redux-like state (Stores + Effects + HubEventDispatcher) |
| `Observability` | Metrics collector, REST API, SignalR hub — serves the Dashboard PWA |
| `Dashboard.Client` | Blazor WebAssembly observability dashboard (token costs, tool analytics, errors, schedules, memory, health) |
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

### Launching

Pick the override file matching your OS:

```bash
# Linux / WSL
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-websearch mcp-idealista mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus qbittorrent jackett redis caddy camoufox

# Windows
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.windows.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-websearch mcp-idealista mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus qbittorrent jackett redis caddy camoufox
```

### Secrets

Services read secrets from .NET User Secrets mounted into containers at `/home/app/.microsoft/usersecrets`. The override files map the host-side path. If the agent crashes with `Value cannot be an empty string. (Parameter 'connectionString')`, user secrets are not being mounted — check you're using the correct override for your OS.

### Accessing the WebChat

Caddy (port 443, Let's Encrypt TLS) is the entry point. It routes `/hubs/*` to the McpChannelSignalR hub, `/dashboard/*` to the Observability service, and everything else to the WebUI. **Connect through Caddy, not directly to webui:5001**, or SignalR won't reach the channel server.

### Accessing the Dashboard

The observability dashboard is available at `https://assistants.herfluffness.com/dashboard/` (via Caddy) or `http://localhost:5003/dashboard/` (direct). It's a PWA that can be installed as a standalone app. The dashboard shows token costs, tool analytics, error rates, schedule history, memory analytics, and live service health. Data flows via Redis Pub/Sub: services emit metric events → the Observability collector aggregates them → the dashboard reads via REST API and receives live updates via SignalR.

### Observability Architecture

Services publish `MetricEvent` DTOs via `IMetricsPublisher` → Redis Pub/Sub channel `metrics:events`. The `MetricsCollectorService` subscribes, aggregates into Redis (sorted sets for time-series, hashes for totals, TTL keys for health), and forwards live events to the SignalR hub (`/hubs/metrics`). `MetricsQueryService` provides grouped aggregation queries over the stored metrics (breakdowns by dimension/metric enums). The dashboard uses a hybrid approach: REST API for historical data on page load, SignalR for real-time updates. Dashboard components (`DynamicChart`, `PillSelector`) use `LocalStorageService` to persist UI state across sessions.

### Memory Architecture

Memory is a built-in agent feature (not a separate MCP server). It runs as services inside the Agent process:
- **Extraction**: `ChatMonitor` queues conversation turns → `MemoryExtractionWorker` processes the queue → `IMemoryExtractor` (LLM-based) identifies memories to store → `IMemoryStore` (Redis Stack with vector search) persists them.
- **Recall**: `MemoryRecallHook` runs before each agent turn, retrieves relevant memories via semantic search, and injects them into the system prompt.
- **Dreaming**: `MemoryDreamingService` periodically consolidates and prunes memories using `IMemoryConsolidator` (LLM-based).
- **Metrics**: Extraction, recall, and dreaming events are published as `MetricEvent` DTOs for the Observability dashboard.

### Channel Architecture

Transports (WebChat, Telegram, ServiceBus) run as independent MCP channel servers. The agent connects to them as an MCP client via `ChannelEndpoints` config. Each channel exposes a standard protocol:
- **Inbound**: `channel/message` notification (user message → agent)
- **Outbound**: `send_reply` tool (agent response → user), `request_approval` tool (tool approval flow)

New transports can be added by deploying a new channel MCP server — zero agent changes needed.

### Camoufox

The `camoufox` Docker service provides an anti-detect browser (Firefox-based) for web scraping. McpServerWebSearch connects to it via WebSocket (`ws://camoufox:9377/browser`). Configuration is in `McpServerWebSearch/Settings/McpSettings.cs` (`CamoufoxConfiguration`).

### Debugging with Playwright

When automating the WebChat with Playwright, use `ignoreHTTPSErrors: true` for the browser context when testing locally (the Let's Encrypt certificate is valid for `assistants.herfluffness.com`, not `localhost`). You must select a user identity from the avatar picker in the header before sending messages, otherwise sends are silently rejected with a toast error.