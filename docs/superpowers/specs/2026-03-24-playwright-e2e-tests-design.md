# Playwright E2E Tests for WebChat and Dashboard

## Goal

Add end-to-end Playwright tests for the WebChat and Dashboard Blazor WebAssembly applications, covering the most common user interactions. Tests run against real containerized services spun up via Testcontainers — no mocking of any kind is allowed.

## No Mocking Rule

E2E tests must not use mocks, stubs, fakes, or any form of test doubles. Every interaction goes through real services in real containers. This applies to all layers: the LLM calls go through real OpenRouter, Redis pub/sub is real, SignalR connections are real, and browser interactions use real Chromium. The Dashboard real-time tests publish real `MetricEvent` DTOs to the Redis `metrics:events` pub/sub channel, which flow through the real Observability `MetricsCollectorService` and SignalR hub.

## Test Infrastructure

### Fixture Hierarchy

- **`E2EFixtureBase`** — Abstract base handling Playwright browser lifecycle: installs Chromium (not Camoufox — no anti-bot needed for our own UI), creates `IBrowser` and provides fresh `IPage` instances per test. Implements `IAsyncLifetime`.
- **`WebChatE2EFixture`** — Extends base. Uses Testcontainers to build and start the full WebChat stack (see Container Topology below). Exposes the WebChat URL via Caddy. Waits for health checks before yielding to tests.
- **`DashboardE2EFixture`** — Extends base. Uses Testcontainers to build and start: `base-sdk` image → Redis, Observability. Exposes the Dashboard URL (direct access, no Caddy needed). Lighter stack since the Dashboard does not need the agent.

### Container Topology

#### WebChat Stack

Build order matters — `base-sdk` must be built first since most Dockerfiles depend on it.

1. **`base-sdk`** — Pre-builds Domain + Infrastructure layers (build context: repo root, `Dockerfile.base-sdk`)
2. **Redis** — `redis/redis-stack-server:latest`, port 6379
3. **MCP Tool Servers** — All 5 are required because the agent definition references them in `McpServerEndpoints` and the agent fails to start if any endpoint is unreachable:
   - `mcp-library` (build context: repo root, `McpServerLibrary/Dockerfile`)
   - `mcp-text` (build context: repo root, `McpServerText/Dockerfile`)
   - `mcp-websearch` (build context: repo root, `McpServerWebSearch/Dockerfile`)
   - `mcp-memory` (build context: repo root, `McpServerMemory/Dockerfile`)
   - `mcp-idealista` (build context: repo root, `McpServerIdealista/Dockerfile`)
4. **`mcp-channel-signalr`** — SignalR channel server (build context: repo root, `McpChannelSignalR/Dockerfile`)
5. **Agent** — Main agent with `--chat Web --reasoning` command (build context: repo root, `Agent/Dockerfile`)
6. **WebUI** — Blazor WebAssembly host (build context: repo root, `WebChat/Dockerfile`)
7. **Caddy** — Reverse proxy (build context: `DockerCompose/caddy/`). Uses a **test-specific Caddyfile** (see Caddy Test Configuration below).

All containers join a shared Docker network so inter-service DNS works (e.g., `mcp-channel-signalr:8080`). Container names must match the DNS names used in configuration (e.g., the container named `redis` is reachable as `redis` on the network).

#### Dashboard Stack

1. **Redis** — same as above
2. **Observability** — Metrics collector + REST API + SignalR hub (build context: repo root, `Observability/Dockerfile`)

### Caddy Test Configuration

The production `Caddyfile` uses domain-based virtual hosting with Cloudflare DNS challenge TLS, which won't work in tests. The fixture creates a test-specific `Caddyfile` that:

- Listens on `:80` (plain HTTP, no TLS)
- Routes `/hubs/*` → `mcp-channel-signalr:8080`
- Routes `/api/agents*` → `agent:8080`
- Routes everything else → `webui:8080`

This preserves the same routing structure that the WebChat depends on (SignalR hub at `/hubs/*` relative to page origin) without requiring certificates or DNS.

Example test Caddyfile:
```
:80 {
    handle /hubs/* {
        reverse_proxy mcp-channel-signalr:8080
    }
    handle /api/agents* {
        reverse_proxy agent:8080
    }
    handle {
        reverse_proxy webui:8080
    }
}
```

The Caddy container maps port 80 to a random host port. The fixture reads this mapped port and uses `http://localhost:{port}` as the WebChat URL for Playwright.

### Secrets and Configuration

#### OpenRouter API Key

The `OPENROUTER__APIKEY` is required for the real agent. The fixture reads it from:
1. Environment variable `OPENROUTER__APIKEY` (for CI)
2. .NET User Secrets (for local development, matching the existing pattern in `PlaywrightWebBrowserFixture`)

If the key is not available, tests are skipped with `Skip.If()` (using the existing `Xunit.SkippableFact` package).

#### Agent Definition

The fixture configures a test agent definition via environment variables on the Agent container:

```
AGENTS__0__ID=test-agent
AGENTS__0__NAME=Test Agent
AGENTS__0__MODEL=openai/gpt-4o-mini  (cheap, fast model for tests)
AGENTS__0__MCPSERVERENDPOINTS__0=http://mcp-library:8080/sse
AGENTS__0__MCPSERVERENDPOINTS__1=http://mcp-text:8080/sse
AGENTS__0__MCPSERVERENDPOINTS__2=http://mcp-websearch:8080/sse
AGENTS__0__MCPSERVERENDPOINTS__3=http://mcp-memory:8080/sse
AGENTS__0__MCPSERVERENDPOINTS__4=http://mcp-idealista:8080/sse
AGENTS__0__WHITELISTPATTERNS__0=memory_*  (only memory tools auto-approved; others trigger approval modal)
CHANNELENDPOINTS__0__CHANNELID=Web
CHANNELENDPOINTS__0__ENDPOINT=http://mcp-channel-signalr:8080/sse
```

The `WhitelistPatterns` is set to only auto-approve memory tools. Any other tool call (e.g., web search, file operations) will trigger the approval modal, making approval tests deterministic.

#### User Identity Configuration

The WebUI container needs user identities configured so the avatar picker has users to display:

```
USERS__0__ID=TestUser
USERS__0__AVATARURL=https://api.dicebear.com/7.x/bottts/svg?seed=test
```

The fixture also sets `AGENTURL=http://caddy:80` on the WebUI container so the Blazor client knows where to connect for SignalR (relative to the Caddy proxy).

#### Redis Configuration

```
REDIS__CONNECTIONSTRING=redis:6379
```

Set on Agent, SignalR channel, and Observability containers.

### Playwright Setup

- Uses `Microsoft.Playwright` NuGet package. Add a direct package reference to `Tests.csproj` since the test APIs (`BrowserType.LaunchAsync`, `Page.GotoAsync`, etc.) need to be directly available.
- Standard Chromium browser, headless mode.
- No `ignoreHTTPSErrors` needed since tests go through the HTTP-only test Caddy (for WebChat) or directly to Observability (for Dashboard).
- Each test gets a fresh `IPage` (new browser tab) but shares the container stack.

### Timeouts and Resource Management

- **Container startup timeout**: 5 minutes for the full WebChat stack (building images + starting ~10 containers + waiting for health checks). The xUnit collection fixture `InitializeAsync` uses a generous `CancellationTokenSource`.
- **`base-sdk` image**: Built once per test run. Both fixtures share the same image name (`base-sdk:latest`) and use `WithDeleteIfExists(false)` + `WithCleanUp(false)` to preserve Docker layer cache across runs (same pattern as `PlaywrightWebBrowserFixture`).
- **xUnit timeout**: Configure `[Trait("Timeout", "600000")]` or use `xunit.runner.json` settings for E2E test timeout.
- **Test parallelism**: WebChat test classes (`WebChatE2ETests` and `WebChatTopicManagementE2ETests`) share one xUnit collection fixture. Dashboard test classes share another. Tests within a collection run sequentially (xUnit convention for collection fixtures). The two collections can run in parallel since they use independent container stacks.
- **Memory**: The WebChat stack needs ~10 containers. Recommend at least 8GB RAM available for Docker.

### Diagnostics on Failure

- **Container logs**: On fixture `DisposeAsync`, if any test failed, dump container logs to the test output via `ITestOutputHelper`.
- **Screenshots**: Each test wraps assertions in a try/catch that takes a Playwright screenshot on failure, saved to the test output directory.
- **Headed mode**: Tests can be run in headed mode for local debugging by setting environment variable `PLAYWRIGHT_HEADLESS=false`.

### Container Build Context

All service Dockerfiles use `COPY` commands relative to the repository root (e.g., `COPY ["Agent/Agent.csproj", "Agent/"]`). When using `ImageFromDockerfileBuilder`, the build context must be set to the repository root directory, not the individual project directory. The `FindSolutionRoot()` helper from the existing `PlaywrightWebBrowserFixture` can be reused for this purpose.

## WebChat Test Scenarios

### `WebChatE2ETests` (core chat flow)

| Test | What it verifies |
|------|-----------------|
| `LoadPage_ShowsAvatarPickerAndInput` | Page loads, avatar placeholder (`?`) is visible, chat input is visible but disabled |
| `SelectUser_AvatarUpdates` | Click `.avatar-button` → dropdown `.user-dropdown-menu` opens → click `.user-dropdown-item` → avatar `img.avatar-image` replaces placeholder, chat input becomes enabled |
| `SendMessage_AppearsInChat` | Select user → type in `textarea.chat-input` → press Enter → user message appears in message list → wait for streaming to complete → agent response text appears |
| `SendMessage_CreatesTopicInSidebar` | After sending the first message, a new entry appears in the topic list sidebar |
| `CancelStreaming_StopsResponse` | Send a message → wait for the Cancel button to appear (`.btn-secondary` with text "Cancel", which signals streaming has started) → click Cancel → streaming indicator disappears. The Cancel button's visibility is the reliable signal that streaming is active. |
| `ApprovalModal_ApproveFlow` | Send a message that triggers a tool call not in the whitelist (e.g., "search the web for today's weather") → wait for `.approval-modal` to appear → click Approve → agent continues and eventually responds |
| `ApprovalModal_DenyFlow` | Same trigger → wait for `.approval-modal` → click Deny → agent handles denial gracefully with a response indicating the tool was denied |
| `ConnectionStatus_ShowsConnected` | After page load and SignalR connection, the `.connection-status` element shows the connected state |

### `WebChatTopicManagementE2ETests`

| Test | What it verifies |
|------|-----------------|
| `SelectTopic_LoadsMessages` | Send a message (creates topic) → send another message in a new topic → click the first topic in sidebar → its messages are displayed |
| `DeleteTopic_RemovesFromSidebar` | Create a topic via message → delete it → topic disappears from the sidebar |

## Dashboard Test Scenarios

### `DashboardOverviewE2ETests`

The Dashboard is accessed directly at `http://localhost:{observability-mapped-port}/` (no Caddy, no path prefix).

| Test | What it verifies |
|------|-----------------|
| `LoadOverview_ShowsKpiCards` | Page loads, 5 KPI cards are visible: Input Tokens, Output Tokens, Cost, Tool Calls, Errors |
| `LoadOverview_ShowsHealthGrid` | Service Health section renders with a health grid component |
| `LoadOverview_ShowsConnectionStatus` | "Live" or "Disconnected" badge `.connection-status` is visible in the header |
| `TimeFilter_ChangesData` | Click the "7d" pill in `PillSelector` → KPI values update to reflect the 7-day range |

### `DashboardNavigationE2ETests`

| Test | What it verifies |
|------|-----------------|
| `NavigateToTokens_ShowsTokenPage` | Click Tokens nav link → Tokens page renders with a chart component |
| `NavigateToTools_ShowsToolsPage` | Click Tools nav link → Tools page renders |
| `NavigateToErrors_ShowsErrorsPage` | Click Errors nav link → Errors page renders |
| `NavigateToSchedules_ShowsSchedulesPage` | Click Schedules nav link → Schedules page renders |

### `DashboardRealTimeE2ETests`

These tests connect directly to the Redis container to publish events, which then flow through the real Observability service.

| Test | What it verifies |
|------|-----------------|
| `LiveMetrics_UpdateWithoutRefresh` | Connect to Redis via `StackExchange.Redis` → publish a `MetricEvent` (token usage event) to `metrics:events` channel → wait for KPI card value to change on the page without refresh |
| `HealthGrid_ReflectsServiceStatus` | Publish a health heartbeat event to Redis → health grid updates to show the new service status |

## Project Structure

```
Tests/
  E2E/
    Fixtures/
      E2EFixtureBase.cs
      WebChatE2EFixture.cs
      DashboardE2EFixture.cs
    WebChat/
      WebChatE2ETests.cs
      WebChatTopicManagementE2ETests.cs
    Dashboard/
      DashboardOverviewE2ETests.cs
      DashboardNavigationE2ETests.cs
      DashboardRealTimeE2ETests.cs
```

## Conventions

- Test naming: `Method_Scenario_ExpectedResult` (existing project convention).
- Assertions: `Shouldly`.
- Framework: `xUnit` with collection fixtures for shared container lifecycle.
- Tests tagged with `[Trait("Category", "E2E")]` for selective runs via `dotnet test --filter Category=E2E`.
- No mocking — all services are real containers.

## Dependencies

Add to `Tests.csproj`:
- `Microsoft.Playwright` — direct reference needed for Playwright test APIs (`BrowserType.LaunchAsync`, `IPage`, etc.)

Already present (no changes needed):
- `Testcontainers`
- `xUnit`, `Shouldly`
- `Xunit.SkippableFact` (for skipping when API key is unavailable)
- `StackExchange.Redis` (transitive via Infrastructure, for Dashboard real-time tests publishing to Redis)

## Side Fix

Update `Observability/Dockerfile` to use `base-sdk:latest` as the build base, matching all other service Dockerfiles. Currently it uses a standalone `mcr.microsoft.com/dotnet/sdk:10.0` stage, which is inconsistent and means it doesn't share the pre-built Domain + Infrastructure layers.
