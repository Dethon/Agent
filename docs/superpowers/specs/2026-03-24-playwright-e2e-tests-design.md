# Playwright E2E Tests for WebChat and Dashboard

## Goal

Add end-to-end Playwright tests for the WebChat and Dashboard Blazor WebAssembly applications, covering the most common user interactions. Tests run against real containerized services spun up via Testcontainers — no mocking allowed.

## Test Infrastructure

### Fixture Hierarchy

- **`E2EFixtureBase`** — Abstract base handling Playwright browser lifecycle: installs Chromium (not Camoufox — no anti-bot needed for our own UI), creates `IBrowser` and provides fresh `IPage` instances per test. Implements `IAsyncLifetime`.
- **`WebChatE2EFixture`** — Extends base. Uses Testcontainers to build and start: `base-sdk` image → Redis, all MCP tool servers (Library, Text, WebSearch, Memory, Idealista), SignalR channel, Agent (with `--chat Web --reasoning` and a real LLM via OpenRouter), WebUI, and Caddy. Exposes the WebChat URL. Waits for health checks before yielding to tests.
- **`DashboardE2EFixture`** — Extends base. Uses Testcontainers to build and start: `base-sdk` image → Redis, Observability. Exposes the Dashboard URL. Lighter stack since the Dashboard does not need the agent.

### Container Orchestration

- All containers join a shared Docker network so inter-service DNS works (e.g., `mcp-channel-signalr:8080`).
- The `base-sdk` image is built first as a prerequisite since Agent, WebUI, SignalR channel, and (after fix) Observability Dockerfiles depend on it.
- Containers use random host port bindings. The fixture reads mapped ports to construct URLs for Playwright.
- Container lifecycle is managed by xUnit collection fixtures — one per app, shared across all test classes in the collection.

### Playwright Setup

- Uses `Microsoft.Playwright` NuGet package (already in the project transitively).
- Standard Chromium browser, headless mode.
- `ignoreHTTPSErrors: true` on the browser context for Caddy's Let's Encrypt certificates.
- Each test gets a fresh `IPage` (new browser tab) but shares the container stack.

### No Mocking Rule

E2E tests must not use mocks, stubs, or fakes of any kind. All interactions go through real services in real containers. The Dashboard real-time tests publish real events to Redis pub/sub, which flow through the real Observability service's `MetricsCollectorService` and SignalR hub.

## WebChat Test Scenarios

### `WebChatE2ETests` (core chat flow)

| Test | What it verifies |
|------|-----------------|
| `LoadPage_ShowsAvatarPickerAndInput` | Page loads, avatar placeholder (`?`) is visible, chat input is visible but disabled |
| `SelectUser_AvatarUpdates` | Click avatar button → dropdown opens → select a user → avatar image replaces placeholder, chat input becomes enabled |
| `SendMessage_AppearsInChat` | Select user → type message → press Enter → user message appears in message list → agent streams a response → response text appears |
| `SendMessage_CreatesTopicInSidebar` | After sending the first message, a new topic appears in the sidebar topic list |
| `CancelStreaming_StopsResponse` | Send message → while agent is streaming, click Cancel button → streaming stops |
| `ApprovalModal_ApproveFlow` | Agent requests tool approval → modal appears with tool details → click Approve → agent continues execution |
| `ApprovalModal_DenyFlow` | Agent requests tool approval → modal appears → click Deny → agent handles the denial gracefully |
| `ConnectionStatus_ShowsConnected` | After page load and SignalR connection, the connection status indicator shows the connected state |

### `WebChatTopicManagementE2ETests`

| Test | What it verifies |
|------|-----------------|
| `SelectTopic_LoadsMessages` | Send a message (creates topic) → send another message in a new topic → click the first topic → its messages are displayed |
| `DeleteTopic_RemovesFromSidebar` | Create a topic via message → delete it → topic disappears from the sidebar |

## Dashboard Test Scenarios

### `DashboardOverviewE2ETests`

| Test | What it verifies |
|------|-----------------|
| `LoadOverview_ShowsKpiCards` | Page loads, 5 KPI cards are visible: Input Tokens, Output Tokens, Cost, Tool Calls, Errors |
| `LoadOverview_ShowsHealthGrid` | Service Health section renders with a health grid component |
| `LoadOverview_ShowsConnectionStatus` | "Live" or "Disconnected" badge is visible in the header |
| `TimeFilter_ChangesData` | Click the "7d" pill → KPI values update to reflect the 7-day range |

### `DashboardNavigationE2ETests`

| Test | What it verifies |
|------|-----------------|
| `NavigateToTokens_ShowsTokenPage` | Click Tokens nav link → Tokens page renders with a chart component |
| `NavigateToTools_ShowsToolsPage` | Click Tools nav link → Tools page renders |
| `NavigateToErrors_ShowsErrorsPage` | Click Errors nav link → Errors page renders |
| `NavigateToSchedules_ShowsSchedulesPage` | Click Schedules nav link → Schedules page renders |

### `DashboardRealTimeE2ETests`

| Test | What it verifies |
|------|-----------------|
| `LiveMetrics_UpdateWithoutRefresh` | Publish a `MetricEvent` to Redis `metrics:events` channel → Dashboard KPI cards update without page refresh (SignalR push) |
| `HealthGrid_ReflectsServiceStatus` | Publish health events to Redis → health grid updates to reflect service status changes |

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

No new NuGet packages needed. The project already has:
- `Microsoft.Playwright` (transitive via Infrastructure)
- `Testcontainers`
- `xUnit`, `Shouldly`

If the transitive Playwright reference is insufficient for test APIs, add a direct `Microsoft.Playwright` package reference to `Tests.csproj`.

## Side Fix

Update `Observability/Dockerfile` to use `base-sdk:latest` as the build base, matching all other service Dockerfiles. Currently it uses a standalone `mcr.microsoft.com/dotnet/sdk:10.0` stage, which is inconsistent and means it doesn't share the pre-built Domain + Infrastructure layers.
