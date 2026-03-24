# Playwright E2E Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Playwright E2E tests for WebChat and Dashboard, running against real containerized services via Testcontainers.

**Architecture:** Testcontainers builds and starts Docker containers for each app stack. A Playwright Chromium browser interacts with the UIs. WebChat tests use a test-specific Caddy config for HTTP routing. Dashboard tests connect directly to Observability. No mocking of any kind.

**Tech Stack:** .NET 10, xUnit, Shouldly, Testcontainers, Microsoft.Playwright, StackExchange.Redis

**Spec:** `docs/superpowers/specs/2026-03-24-playwright-e2e-tests-design.md`

**Deviation from spec:** The spec says all 5 MCP tool servers are needed. Analysis shows we control the agent definition via env vars, so we only include servers that can start without external API keys. The test agent uses `mcp-text` (needs only a vault path) — enough to chat and trigger tool approval. This cuts the stack from ~12 containers to ~7.

---

## Task 1: Side fix — Update Observability Dockerfile to use base-sdk

**Files:**
- Modify: `Observability/Dockerfile`

- [ ] **Step 1: Rewrite Observability/Dockerfile to use base-sdk**

The current Dockerfile uses a standalone SDK stage. Rewrite it to match the pattern used by all other service Dockerfiles (Agent, WebUI, McpServers).

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["Dashboard.Client/Dashboard.Client.csproj", "Dashboard.Client/"]
COPY ["Observability/Observability.csproj", "Observability/"]
RUN dotnet restore "Observability/Observability.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Dashboard.Client/", "Dashboard.Client/"]
COPY ["Observability/", "Observability/"]
WORKDIR "/src/Observability"
RUN dotnet build "../Dashboard.Client/Dashboard.Client.csproj" -c $BUILD_CONFIGURATION --no-restore
RUN dotnet publish "./Observability.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Observability.dll"]
```

- [ ] **Step 2: Verify Docker build succeeds**

Run (from repo root):
```bash
docker build -t base-sdk:latest -f Dockerfile.base-sdk . && \
docker build -t observability-test:latest -f Observability/Dockerfile .
```

Expected: Build succeeds, image created.

- [ ] **Step 3: Commit**

```bash
git add Observability/Dockerfile
git commit -m "fix: update Observability Dockerfile to use base-sdk base image"
```

---

## Task 2: Add Microsoft.Playwright package reference

**Files:**
- Modify: `Tests/Tests.csproj`

- [ ] **Step 1: Add Playwright package reference**

Add to the main `<ItemGroup>` with other PackageReferences in `Tests/Tests.csproj`:

```xml
<PackageReference Include="Microsoft.Playwright" Version="1.52.0" />
```

- [ ] **Step 2: Restore and verify build**

```bash
cd /home/dethon/repos/agent && dotnet restore Tests/Tests.csproj && dotnet build Tests/Tests.csproj --no-restore
```

Expected: Build succeeds.

- [ ] **Step 3: Install Playwright browsers**

```bash
pwsh Tests/bin/Debug/net10.0/playwright.ps1 install chromium
```

If `pwsh` is not available, use:
```bash
dotnet tool install --global Microsoft.Playwright.CLI 2>/dev/null; playwright install chromium
```

Expected: Chromium installed.

- [ ] **Step 4: Commit**

```bash
git add Tests/Tests.csproj
git commit -m "chore: add Microsoft.Playwright direct package reference for E2E tests"
```

---

## Task 3: Extract FindSolutionRoot to shared utility

**Files:**
- Create: `Tests/E2E/Fixtures/TestHelpers.cs`
- Modify: `Tests/Integration/Fixtures/PlaywrightWebBrowserFixture.cs`

The existing `FindSolutionRoot()` is `private static` in `PlaywrightWebBrowserFixture`. Extract it so E2E fixtures can reuse it.

- [ ] **Step 1: Create TestHelpers with FindSolutionRoot**

```csharp
namespace Tests.E2E.Fixtures;

internal static class TestHelpers
{
    internal static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find solution root directory.");
    }
}
```

- [ ] **Step 2: Update PlaywrightWebBrowserFixture to use shared helper**

In `Tests/Integration/Fixtures/PlaywrightWebBrowserFixture.cs`, replace the `private static string FindSolutionRoot()` method body with a call to `TestHelpers.FindSolutionRoot()`:

```csharp
private static string FindSolutionRoot() => E2E.Fixtures.TestHelpers.FindSolutionRoot();
```

- [ ] **Step 3: Verify existing tests still compile**

```bash
dotnet build Tests/Tests.csproj
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/Fixtures/TestHelpers.cs Tests/Integration/Fixtures/PlaywrightWebBrowserFixture.cs
git commit -m "refactor: extract FindSolutionRoot to shared TestHelpers for E2E reuse"
```

---

## Task 4: Create E2EFixtureBase — Playwright browser lifecycle

**Files:**
- Create: `Tests/E2E/Fixtures/E2EFixtureBase.cs`

- [ ] **Step 1: Write the failing test**

Create a minimal test to verify the base fixture can launch a browser:

```csharp
// Tests/E2E/Fixtures/E2EFixtureBaseTests.cs
using Shouldly;

namespace Tests.E2E.Fixtures;

public class E2EFixtureBaseTests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task CreatePage_ReturnsUsablePage()
    {
        // Use a concrete subclass that doesn't need containers
        await using var fixture = new StandalonePlaywrightFixture();
        await fixture.InitializeAsync();

        var page = await fixture.CreatePageAsync();

        page.ShouldNotBeNull();
        await page.GotoAsync("about:blank");
        var title = await page.TitleAsync();
        title.ShouldNotBeNull();
    }
}

// Minimal concrete subclass for testing the base
file class StandalonePlaywrightFixture : E2EFixtureBase
{
    public string BaseUrl => "about:blank";
    protected override Task StartContainersAsync(CancellationToken ct) => Task.CompletedTask;
    protected override Task StopContainersAsync() => Task.CompletedTask;
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~E2EFixtureBaseTests" -v n
```

Expected: FAIL — `E2EFixtureBase` does not exist.

- [ ] **Step 3: Implement E2EFixtureBase**

```csharp
// Tests/E2E/Fixtures/E2EFixtureBase.cs
using Microsoft.Playwright;

namespace Tests.E2E.Fixtures;

public abstract class E2EFixtureBase : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        var headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false";

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = headless
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await StartContainersAsync(cts.Token);
    }

    public async Task<IPage> CreatePageAsync()
    {
        if (_browser is null)
            throw new InvalidOperationException("Browser not initialized. Call InitializeAsync first.");

        var context = await _browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    protected abstract Task StartContainersAsync(CancellationToken ct);
    protected abstract Task StopContainersAsync();

    public async Task DisposeAsync()
    {
        await StopContainersAsync();

        if (_browser is not null)
            await _browser.DisposeAsync();

        _playwright?.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~E2EFixtureBaseTests" -v n
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Tests/E2E/Fixtures/E2EFixtureBase.cs Tests/E2E/Fixtures/E2EFixtureBaseTests.cs
git commit -m "feat: add E2EFixtureBase with Playwright browser lifecycle"
```

---

## Task 5: Create DashboardE2EFixture — Testcontainers for Dashboard stack

**Files:**
- Create: `Tests/E2E/Fixtures/DashboardE2EFixture.cs`

The Dashboard stack is simpler (Redis + Observability), so implement it first.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/E2E/Dashboard/DashboardFixtureTests.cs
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardFixtureTests(DashboardE2EFixture fixture)
{
    [Fact]
    public async Task Fixture_ProvidesAccessibleDashboardUrl()
    {
        fixture.DashboardUrl.ShouldNotBeNullOrEmpty();

        var page = await fixture.CreatePageAsync();
        var response = await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.ShouldNotBeNull();
        response.Ok.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardFixtureTests" -v n
```

Expected: FAIL — `DashboardE2EFixture` does not exist.

- [ ] **Step 3: Implement DashboardE2EFixture**

```csharp
// Tests/E2E/Fixtures/DashboardE2EFixture.cs
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Tests.E2E.Fixtures;

public class DashboardE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _observability;

    public string DashboardUrl { get; private set; } = "";
    public string RedisConnectionString { get; private set; } = "";

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        var solutionRoot = TestHelpers.FindSolutionRoot();

        _network = new NetworkBuilder()
            .WithName($"e2e-dashboard-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // 1. Build base-sdk image
        var baseSdkImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Dockerfile.base-sdk")
            .WithName("base-sdk:latest")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await baseSdkImage.CreateAsync(ct);

        // 2. Start Redis
        _redis = new ContainerBuilder()
            .WithImage("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        // 3. Build and start Observability
        var observabilityImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Observability/Dockerfile")
            .WithName($"observability-e2e-{Guid.NewGuid():N}")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await observabilityImage.CreateAsync(ct);

        _observability = new ContainerBuilder(observabilityImage)
            .WithNetwork(_network)
            .WithNetworkAliases("observability")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDIS__CONNECTIONSTRING", "redis:6379")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8080))
            .Build();
        await _observability.StartAsync(ct);

        var host = _observability.Hostname;
        var port = _observability.GetMappedPublicPort(8080);
        DashboardUrl = $"http://{host}:{port}/";

        var redisPort = _redis.GetMappedPublicPort(6379);
        RedisConnectionString = $"{_redis.Hostname}:{redisPort}";
    }

    protected override async Task StopContainersAsync()
    {
        if (_observability is not null) await _observability.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}

[CollectionDefinition("DashboardE2E")]
public class DashboardE2ECollection : ICollectionFixture<DashboardE2EFixture>;
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardFixtureTests" -v n --timeout 600000
```

Expected: PASS (may take 2-3 minutes on first run for image builds).

- [ ] **Step 5: Commit**

```bash
git add Tests/E2E/Fixtures/DashboardE2EFixture.cs Tests/E2E/Dashboard/DashboardFixtureTests.cs
git commit -m "feat: add DashboardE2EFixture with Testcontainers for Redis + Observability"
```

---

## Task 6: Dashboard Overview E2E tests

**Files:**
- Create: `Tests/E2E/Dashboard/DashboardOverviewE2ETests.cs`
- Delete: `Tests/E2E/Dashboard/DashboardFixtureTests.cs` (superseded)

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/E2E/Dashboard/DashboardOverviewE2ETests.cs
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardOverviewE2ETests(DashboardE2EFixture fixture)
{
    [Fact]
    public async Task LoadOverview_ShowsKpiCards()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        var kpiCards = page.Locator(".kpi-card");
        var count = await kpiCards.CountAsync();
        count.ShouldBe(5);

        var labels = await kpiCards.Locator(".kpi-label").AllTextContentsAsync();
        labels.ShouldContain("Input Tokens");
        labels.ShouldContain("Output Tokens");
        labels.ShouldContain("Cost");
        labels.ShouldContain("Tool Calls");
        labels.ShouldContain("Errors");
    }

    [Fact]
    public async Task LoadOverview_ShowsHealthGrid()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        var healthGrid = page.Locator(".health-grid");
        (await healthGrid.CountAsync()).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task LoadOverview_ShowsConnectionStatus()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        var status = page.Locator(".connection-status");
        await status.WaitForAsync(new() { Timeout = 10_000 });
        (await status.IsVisibleAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task TimeFilter_ChangesData()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Click the "7d" pill
        var pill7d = page.Locator(".pill-selector button", new() { HasTextString = "7d" });
        await pill7d.ClickAsync();

        // Verify the pill is now active (selected)
        await Assertions.Expect(pill7d).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardOverviewE2ETests" -v n --timeout 600000
```

Expected: FAIL — CSS selectors may not match. Adjust selectors if needed.

- [ ] **Step 3: Fix any selector mismatches based on actual rendered DOM**

Use Playwright's debug mode to inspect the DOM:
```bash
PLAYWRIGHT_HEADLESS=false dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardOverviewE2ETests.LoadOverview_ShowsKpiCards" -v n --timeout 600000
```

Adjust selectors in the test file to match the actual rendered HTML. Common things to check:
- KPI card CSS class (might be scoped: check `Dashboard.Client/Components/KpiCard.razor` for the actual class names)
- PillSelector button class for "active" state
- Health grid CSS class

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardOverviewE2ETests" -v n --timeout 600000
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Delete DashboardFixtureTests.cs (superseded)**

```bash
rm Tests/E2E/Dashboard/DashboardFixtureTests.cs
```

- [ ] **Step 6: Commit**

```bash
git add Tests/E2E/Dashboard/DashboardOverviewE2ETests.cs
git rm Tests/E2E/Dashboard/DashboardFixtureTests.cs
git commit -m "feat: add Dashboard Overview E2E tests (KPI cards, health grid, time filter)"
```

---

## Task 7: Dashboard Navigation E2E tests

**Files:**
- Create: `Tests/E2E/Dashboard/DashboardNavigationE2ETests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/E2E/Dashboard/DashboardNavigationE2ETests.cs
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardNavigationE2ETests(DashboardE2EFixture fixture)
{
    [Theory]
    [InlineData("/tokens", "Tokens")]
    [InlineData("/tools", "Tools")]
    [InlineData("/errors", "Errors")]
    [InlineData("/schedules", "Schedules")]
    public async Task NavigateToPage_ShowsCorrectPage(string href, string expectedTitle)
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Click the sidebar nav link
        var navLink = page.Locator($"nav.sidebar a[href='{href}']");
        await navLink.ClickAsync();

        // Wait for navigation
        await page.WaitForURLAsync($"**{href}");

        // Verify the page header contains the expected title
        var header = page.Locator("h2");
        await Assertions.Expect(header).ToContainTextAsync(expectedTitle);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardNavigationE2ETests" -v n --timeout 600000
```

Expected: FAIL — class/selectors may not match.

- [ ] **Step 3: Adjust selectors if needed and verify tests pass**

Check `Dashboard.Client/Layout/MainLayout.razor` for the actual sidebar link structure. The NavLink `href` values are `/tokens`, `/tools`, `/errors`, `/schedules`. Adjust selectors as needed.

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardNavigationE2ETests" -v n --timeout 600000
```

Expected: All 4 tests PASS.

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/Dashboard/DashboardNavigationE2ETests.cs
git commit -m "feat: add Dashboard Navigation E2E tests"
```

---

## Task 8: Dashboard Real-Time E2E tests

**Files:**
- Create: `Tests/E2E/Dashboard/DashboardRealTimeE2ETests.cs`

These tests publish real events to Redis and verify the Dashboard updates via SignalR without page refresh.

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/E2E/Dashboard/DashboardRealTimeE2ETests.cs
using System.Text.Json;
using Domain.DTOs.Metrics;
using Microsoft.Playwright;
using Shouldly;
using StackExchange.Redis;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardRealTimeE2ETests(DashboardE2EFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task LiveMetrics_UpdateWithoutRefresh()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for SignalR connection (Live badge)
        await page.Locator(".connection-status.connected").WaitForAsync(new() { Timeout = 15_000 });

        // Read current KPI values
        var inputTokensCard = page.Locator(".kpi-card").Filter(new() { HasTextString = "Input Tokens" });
        var initialValue = await inputTokensCard.Locator(".kpi-value").TextContentAsync();

        // Publish a token usage event to Redis
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var subscriber = redis.GetSubscriber();
        var channel = RedisChannel.Literal("metrics:events");

        var tokenEvent = new TokenUsageEvent
        {
            Sender = "e2e-test",
            Model = "test-model",
            InputTokens = 1000,
            OutputTokens = 500,
            Cost = 0.01m,
            AgentId = "test-agent"
        };
        var json = JsonSerializer.Serialize<MetricEvent>(tokenEvent, JsonOptions);
        await subscriber.PublishAsync(channel, json);

        // Wait for the KPI value to change (SignalR push, no page refresh)
        await page.WaitForFunctionAsync(
            $$"""
            () => {
                const cards = document.querySelectorAll('.kpi-card');
                for (const card of cards) {
                    const label = card.querySelector('.kpi-label');
                    const value = card.querySelector('.kpi-value');
                    if (label && label.textContent.includes('Input Tokens') && value && value.textContent !== '{{initialValue}}') {
                        return true;
                    }
                }
                return false;
            }
            """,
            null,
            new() { Timeout = 15_000 });

        var updatedValue = await inputTokensCard.Locator(".kpi-value").TextContentAsync();
        updatedValue.ShouldNotBe(initialValue);
    }

    [Fact]
    public async Task HealthGrid_ReflectsServiceStatus()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for SignalR connection
        await page.Locator(".connection-status.connected").WaitForAsync(new() { Timeout = 15_000 });

        // Publish a heartbeat event
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var subscriber = redis.GetSubscriber();
        var channel = RedisChannel.Literal("metrics:events");

        var heartbeat = new HeartbeatEvent
        {
            Service = "e2e-test-service",
            AgentId = "test-agent"
        };
        var json = JsonSerializer.Serialize<MetricEvent>(heartbeat, JsonOptions);
        await subscriber.PublishAsync(channel, json);

        // Wait for the health grid to show the new service
        var serviceEntry = page.Locator(".health-grid").Locator("text=e2e-test-service");
        await serviceEntry.WaitForAsync(new() { Timeout = 15_000 });
        (await serviceEntry.IsVisibleAsync()).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardRealTimeE2ETests" -v n --timeout 600000
```

Expected: FAIL — either build error or runtime failure.

- [ ] **Step 3: Fix and verify tests pass**

Potential adjustments:
- CSS selectors for KPI cards may use scoped CSS — inspect the DOM
- The `WaitForFunctionAsync` JavaScript selector may need adjustment based on actual DOM structure
- The health grid may use different markup to display service names

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DashboardRealTimeE2ETests" -v n --timeout 600000
```

Expected: Both tests PASS.

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/Dashboard/DashboardRealTimeE2ETests.cs
git commit -m "feat: add Dashboard Real-Time E2E tests (live metrics, health grid)"
```

---

## Task 9: Create WebChatE2EFixture — Testcontainers for WebChat stack

**Files:**
- Create: `Tests/E2E/Fixtures/WebChatE2EFixture.cs`

This is the most complex fixture — it needs Redis, mcp-text, mcp-channel-signalr, Agent, WebUI, and Caddy.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/E2E/WebChat/WebChatFixtureTests.cs
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatFixtureTests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task Fixture_ProvidesAccessibleWebChatUrl()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat E2E stack not available (missing API key?)");

        var page = await fixture.CreatePageAsync();
        var response = await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.ShouldNotBeNull();
        response.Ok.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatFixtureTests" -v n --timeout 600000
```

Expected: FAIL — `WebChatE2EFixture` does not exist.

- [ ] **Step 3: Implement WebChatE2EFixture**

```csharp
// Tests/E2E/Fixtures/WebChatE2EFixture.cs
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Tests.E2E.Fixtures;

public class WebChatE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _mcpText;
    private IContainer? _mcpChannelSignalR;
    private IContainer? _agent;
    private IContainer? _webui;
    private IContainer? _caddy;

    public string WebChatUrl { get; private set; } = "";
    private string? _skipReason;

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        var apiKey = GetOpenRouterApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            _skipReason = "OPENROUTER__APIKEY not available";
            return;
        }

        var solutionRoot = TestHelpers.FindSolutionRoot();

        _network = new NetworkBuilder()
            .WithName($"e2e-webchat-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // 1. Build base-sdk image (shared, cached across runs)
        var baseSdkImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Dockerfile.base-sdk")
            .WithName("base-sdk:latest")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await baseSdkImage.CreateAsync(ct);

        // 2. Start Redis
        _redis = new ContainerBuilder()
            .WithImage("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        // 3. Build and start mcp-text
        var mcpTextImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("McpServerText/Dockerfile")
            .WithName($"mcp-text-e2e-{Guid.NewGuid():N}")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await mcpTextImage.CreateAsync(ct);

        _mcpText = new ContainerBuilder(mcpTextImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-text")
            .WithEnvironment("VAULTPATH", "/vault")
            .Build();
        await _mcpText.StartAsync(ct);

        // 4. Build and start mcp-channel-signalr
        var signalRImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("McpChannelSignalR/Dockerfile")
            .WithName($"mcp-channel-signalr-e2e-{Guid.NewGuid():N}")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await signalRImage.CreateAsync(ct);

        _mcpChannelSignalR = new ContainerBuilder(signalRImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mcp-channel-signalr")
            .WithEnvironment("REDISCONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "test-agent")
            .WithEnvironment("AGENTS__0__NAME", "Test Agent")
            .Build();
        await _mcpChannelSignalR.StartAsync(ct);

        // 5. Build and start Agent
        var agentImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Agent/Dockerfile")
            .WithName($"agent-e2e-{Guid.NewGuid():N}")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await agentImage.CreateAsync(ct);

        _agent = new ContainerBuilder(agentImage)
            .WithNetwork(_network)
            .WithNetworkAliases("agent")
            .WithCommand("--chat", "Web", "--reasoning")
            .WithEnvironment("OPENROUTER__APIURL", "https://openrouter.ai/api/v1")
            .WithEnvironment("OPENROUTER__APIKEY", apiKey)
            .WithEnvironment("REDIS__CONNECTIONSTRING", "redis:6379")
            .WithEnvironment("AGENTS__0__ID", "test-agent")
            .WithEnvironment("AGENTS__0__NAME", "Test Agent")
            .WithEnvironment("AGENTS__0__MODEL", "openai/gpt-4o-mini")
            .WithEnvironment("AGENTS__0__MCPSERVERENDPOINTS__0", "http://mcp-text:8080/sse")
            .WithEnvironment("AGENTS__0__WHITELISTPATTERNS__0", "__none__")
            .WithEnvironment("CHANNELENDPOINTS__0__CHANNELID", "Web")
            .WithEnvironment("CHANNELENDPOINTS__0__ENDPOINT", "http://mcp-channel-signalr:8080/sse")
            .Build();
        await _agent.StartAsync(ct);

        // 6. Build and start WebUI
        var webuiImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("WebChat/Dockerfile")
            .WithName($"webui-e2e-{Guid.NewGuid():N}")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await webuiImage.CreateAsync(ct);

        _webui = new ContainerBuilder(webuiImage)
            .WithNetwork(_network)
            .WithNetworkAliases("webui")
            .WithEnvironment("USERS__0__ID", "TestUser")
            .WithEnvironment("USERS__0__AVATARURL", "https://api.dicebear.com/7.x/bottts/svg?seed=test")
            .Build();
        await _webui.StartAsync(ct);

        // 7. Start Caddy with test-specific Caddyfile
        var testCaddyfile = ":80 {\n" +
            "    handle /hubs/* {\n" +
            "        reverse_proxy mcp-channel-signalr:8080\n" +
            "    }\n" +
            "    handle /api/agents* {\n" +
            "        reverse_proxy agent:8080\n" +
            "    }\n" +
            "    handle {\n" +
            "        reverse_proxy webui:8080\n" +
            "    }\n" +
            "}\n";

        var caddyfilePath = Path.Combine(Path.GetTempPath(), $"Caddyfile-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(caddyfilePath, testCaddyfile, ct);

        _caddy = new ContainerBuilder()
            .WithImage("caddy:2-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("caddy")
            .WithPortBinding(80, true)
            .WithResourceMapping(caddyfilePath, "/etc/caddy/Caddyfile")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(80))
            .Build();
        await _caddy.StartAsync(ct);

        var host = _caddy.Hostname;
        var port = _caddy.GetMappedPublicPort(80);
        WebChatUrl = $"http://{host}:{port}/";
    }

    private static string? GetOpenRouterApiKey()
    {
        // 1. Environment variable (CI)
        var envKey = Environment.GetEnvironmentVariable("OPENROUTER__APIKEY");
        if (!string.IsNullOrEmpty(envKey))
            return envKey;

        // 2. .NET User Secrets
        try
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddUserSecrets("bae64127-c00e-4499-8325-0fb6b452133c")
                .Build();
            return config["OpenRouter:ApiKey"];
        }
        catch
        {
            return null;
        }
    }

    protected override async Task StopContainersAsync()
    {
        if (_caddy is not null) await _caddy.DisposeAsync();
        if (_webui is not null) await _webui.DisposeAsync();
        if (_agent is not null) await _agent.DisposeAsync();
        if (_mcpChannelSignalR is not null) await _mcpChannelSignalR.DisposeAsync();
        if (_mcpText is not null) await _mcpText.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}

[CollectionDefinition("WebChatE2E")]
public class WebChatE2ECollection : ICollectionFixture<WebChatE2EFixture>;
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatFixtureTests" -v n --timeout 600000
```

Expected: PASS (or SKIP if no API key available). First run may take 3-5 minutes for image builds.

- [ ] **Step 5: Commit**

```bash
git add Tests/E2E/Fixtures/WebChatE2EFixture.cs Tests/E2E/WebChat/WebChatFixtureTests.cs
git commit -m "feat: add WebChatE2EFixture with Testcontainers for full chat stack"
```

---

## Task 10: WebChat core E2E tests — page load, user selection, connection status

**Files:**
- Create: `Tests/E2E/WebChat/WebChatE2ETests.cs`
- Delete: `Tests/E2E/WebChat/WebChatFixtureTests.cs` (superseded)

Start with the simpler tests that don't require agent interaction.

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/E2E/WebChat/WebChatE2ETests.cs
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task LoadPage_ShowsAvatarPickerAndInput()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Avatar placeholder (?) should be visible
        var avatarPlaceholder = page.Locator(".avatar-placeholder");
        (await avatarPlaceholder.IsVisibleAsync()).ShouldBeTrue();

        // Chat input should be visible but disabled
        var chatInput = page.Locator("textarea.chat-input");
        (await chatInput.IsVisibleAsync()).ShouldBeTrue();
        (await chatInput.IsDisabledAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task SelectUser_AvatarUpdates()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Click avatar button to open dropdown
        await page.Locator(".avatar-button").ClickAsync();

        // Dropdown should appear
        var dropdown = page.Locator(".user-dropdown-menu");
        await dropdown.WaitForAsync(new() { Timeout = 5_000 });

        // Select the test user
        await page.Locator(".user-dropdown-item").First.ClickAsync();

        // Avatar image should replace placeholder
        var avatarImage = page.Locator("img.avatar-image");
        await avatarImage.WaitForAsync(new() { Timeout = 5_000 });
        (await avatarImage.IsVisibleAsync()).ShouldBeTrue();

        // Chat input should now be enabled (once agent is also selected)
        // Wait a bit for state propagation
        await page.WaitForTimeoutAsync(1_000);
    }

    [SkippableFact]
    public async Task ConnectionStatus_ShowsConnected()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Connection status should eventually show connected
        // The exact selector depends on the MainLayout rendering
        var connectedIndicator = page.Locator(".connection-status.connected, .status-connected, [class*='connected']");
        await connectedIndicator.First.WaitForAsync(new() { Timeout = 15_000 });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests" -v n --timeout 600000
```

Expected: Tests fail or skip (depending on API key availability).

- [ ] **Step 3: Fix selectors based on actual rendered DOM**

Use headed mode to inspect:
```bash
PLAYWRIGHT_HEADLESS=false dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests.LoadPage_ShowsAvatarPickerAndInput" -v n --timeout 600000
```

Adjust CSS selectors to match the actual rendered Blazor HTML. Key things to check:
- Does `.avatar-placeholder` exist? Check `UserIdentityPicker.razor` — it renders `<div class="avatar-placeholder">?</div>`.
- Does the chat input need an agent to be selected first before it's enabled? Check `ChatInput.razor` — it's disabled when `_selectedAgentId` is null.
- The connection status class might differ from what we expect.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests" -v n --timeout 600000
```

Expected: All 3 tests PASS (or SKIP).

- [ ] **Step 5: Delete WebChatFixtureTests.cs and commit**

```bash
rm Tests/E2E/WebChat/WebChatFixtureTests.cs
git add Tests/E2E/WebChat/WebChatE2ETests.cs
git rm Tests/E2E/WebChat/WebChatFixtureTests.cs
git commit -m "feat: add WebChat E2E tests for page load, user selection, connection status"
```

---

## Task 11: WebChat E2E tests — send message, topic creation

**Files:**
- Modify: `Tests/E2E/WebChat/WebChatE2ETests.cs`

- [ ] **Step 1: Add the failing tests**

Add these tests to `WebChatE2ETests`:

```csharp
[SkippableFact]
public async Task SendMessage_AppearsInChat()
{
    Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

    var page = await fixture.CreatePageAsync();
    await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

    await SelectUserAndAgentAsync(page);

    // Type and send message
    var chatInput = page.Locator("textarea.chat-input");
    await chatInput.FillAsync("Hello, this is an E2E test message");
    await chatInput.PressAsync("Enter");

    // User message should appear in the message list
    var userMessage = page.Locator(".message-content", new() { HasTextString = "Hello, this is an E2E test message" });
    await userMessage.WaitForAsync(new() { Timeout = 10_000 });

    // Wait for agent response (streaming completes)
    // The agent response will be an assistant message with .message-content
    var assistantMessages = page.Locator(".chat-message.assistant .message-content, .message-row.assistant .message-content");
    await assistantMessages.First.WaitForAsync(new() { Timeout = 60_000 });
    var responseText = await assistantMessages.First.TextContentAsync();
    responseText.ShouldNotBeNullOrEmpty();
}

[SkippableFact]
public async Task SendMessage_CreatesTopicInSidebar()
{
    Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

    var page = await fixture.CreatePageAsync();
    await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

    await SelectUserAndAgentAsync(page);

    // Send a message to create a topic
    var chatInput = page.Locator("textarea.chat-input");
    await chatInput.FillAsync("Create a topic for E2E testing");
    await chatInput.PressAsync("Enter");

    // A new topic should appear in the sidebar
    var topicItem = page.Locator(".topic-item");
    await topicItem.First.WaitForAsync(new() { Timeout = 30_000 });
    (await topicItem.CountAsync()).ShouldBeGreaterThan(0);
}

internal static async Task SelectUserAndAgentAsync(IPage page)
{
    // Select user identity
    await page.Locator(".avatar-button").ClickAsync();
    await page.Locator(".user-dropdown-item").First.WaitForAsync(new() { Timeout = 5_000 });
    await page.Locator(".user-dropdown-item").First.ClickAsync();

    // Select agent (if dropdown exists and no agent is selected)
    var agentDropdown = page.Locator(".dropdown-trigger");
    if (await agentDropdown.IsVisibleAsync())
    {
        await agentDropdown.ClickAsync();
        var agentItem = page.Locator(".dropdown-item").First;
        await agentItem.WaitForAsync(new() { Timeout = 5_000 });
        await agentItem.ClickAsync();
    }

    // Wait for chat input to become enabled
    var chatInput = page.Locator("textarea.chat-input");
    await Assertions.Expect(chatInput).ToBeEnabledAsync(new() { Timeout = 10_000 });
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests.SendMessage" -v n --timeout 600000
```

Expected: FAIL.

- [ ] **Step 3: Fix selectors and verify tests pass**

Key areas to check:
- How messages are rendered (class names for user vs assistant messages)
- The `SelectUserAndAgentAsync` helper — agent selector might need different selectors
- Timeout for LLM response (60s should be enough for gpt-4o-mini)

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests.SendMessage" -v n --timeout 600000
```

Expected: Both tests PASS.

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/WebChat/WebChatE2ETests.cs
git commit -m "feat: add WebChat E2E tests for sending messages and topic creation"
```

---

## Task 12: WebChat E2E tests — cancel streaming

**Files:**
- Modify: `Tests/E2E/WebChat/WebChatE2ETests.cs`

- [ ] **Step 1: Add the failing test**

```csharp
[SkippableFact]
public async Task CancelStreaming_StopsResponse()
{
    Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

    var page = await fixture.CreatePageAsync();
    await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

    await SelectUserAndAgentAsync(page);

    // Send a message that will trigger a long response
    var chatInput = page.Locator("textarea.chat-input");
    await chatInput.FillAsync("Write a very long and detailed story about a space adventure");
    await chatInput.PressAsync("Enter");

    // Wait for Cancel button to appear (signals streaming has started)
    var cancelButton = page.Locator("button.btn-secondary", new() { HasTextString = "Cancel" });
    await cancelButton.WaitForAsync(new() { Timeout = 30_000 });

    // Click Cancel
    await cancelButton.ClickAsync();

    // Cancel button should disappear (streaming stopped)
    await Assertions.Expect(cancelButton).ToBeHiddenAsync(new() { Timeout = 10_000 });

    // Send button should reappear
    var sendButton = page.Locator("button.btn-primary", new() { HasTextString = "Send" });
    await Assertions.Expect(sendButton).ToBeVisibleAsync(new() { Timeout = 5_000 });
}
```

- [ ] **Step 2: Run test and fix selectors**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests.CancelStreaming" -v n --timeout 600000
```

- [ ] **Step 3: Verify test passes**

Expected: PASS. The Cancel button in `ChatInput.razor` appears when `_isStreaming` is true.

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/WebChat/WebChatE2ETests.cs
git commit -m "feat: add WebChat E2E test for cancel streaming"
```

---

## Task 13: WebChat E2E tests — approval modal flows

**Files:**
- Modify: `Tests/E2E/WebChat/WebChatE2ETests.cs`

The test agent has `WhitelistPatterns = ["__none__"]` so every tool call triggers approval. Sending a message like "what files are in the vault" will make the agent try to use text tools, triggering the modal.

- [ ] **Step 1: Add the failing tests**

```csharp
[SkippableFact]
public async Task ApprovalModal_ApproveFlow()
{
    Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

    var page = await fixture.CreatePageAsync();
    await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

    await SelectUserAndAgentAsync(page);

    // Send a message that triggers a tool call
    var chatInput = page.Locator("textarea.chat-input");
    await chatInput.FillAsync("List all files in the vault");
    await chatInput.PressAsync("Enter");

    // Wait for approval modal to appear
    var approvalModal = page.Locator(".approval-modal");
    await approvalModal.WaitForAsync(new() { Timeout = 60_000 });

    // Verify tool name is shown
    var toolName = page.Locator(".tool-name");
    (await toolName.TextContentAsync()).ShouldNotBeNullOrEmpty();

    // Click Approve
    await page.Locator(".btn-approve").ClickAsync();

    // Modal should dismiss
    await Assertions.Expect(approvalModal).ToBeHiddenAsync(new() { Timeout = 10_000 });

    // Agent should eventually respond
    var assistantMessage = page.Locator(".chat-message.assistant .message-content, .message-row.assistant .message-content");
    await assistantMessage.First.WaitForAsync(new() { Timeout = 60_000 });
}

[SkippableFact]
public async Task ApprovalModal_DenyFlow()
{
    Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

    var page = await fixture.CreatePageAsync();
    await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

    await SelectUserAndAgentAsync(page);

    // Send a message that triggers a tool call
    var chatInput = page.Locator("textarea.chat-input");
    await chatInput.FillAsync("Search for documents in the vault");
    await chatInput.PressAsync("Enter");

    // Wait for approval modal
    var approvalModal = page.Locator(".approval-modal");
    await approvalModal.WaitForAsync(new() { Timeout = 60_000 });

    // Click Reject
    await page.Locator(".btn-reject").ClickAsync();

    // Modal should dismiss
    await Assertions.Expect(approvalModal).ToBeHiddenAsync(new() { Timeout = 10_000 });

    // Agent should respond (handling the rejection)
    var assistantMessage = page.Locator(".chat-message.assistant .message-content, .message-row.assistant .message-content");
    await assistantMessage.First.WaitForAsync(new() { Timeout = 60_000 });
}
```

- [ ] **Step 2: Run tests and fix selectors**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatE2ETests.ApprovalModal" -v n --timeout 600000
```

- [ ] **Step 3: Verify tests pass**

Key areas to debug if failing:
- Does the LLM actually try to call a tool? The prompt "List all files in the vault" should trigger a glob/search tool from mcp-text
- Check `ApprovalModal.razor` for exact button classes: `.btn-approve`, `.btn-reject`
- The approval modal class is `.approval-modal` (inside `.approval-modal-overlay`)

Expected: Both tests PASS.

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/WebChat/WebChatE2ETests.cs
git commit -m "feat: add WebChat E2E tests for approval modal approve and deny flows"
```

---

## Task 14: WebChat Topic Management E2E tests

**Files:**
- Create: `Tests/E2E/WebChat/WebChatTopicManagementE2ETests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/E2E/WebChat/WebChatTopicManagementE2ETests.cs
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatTopicManagementE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task SelectTopic_LoadsMessages()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page);

        // Send first message (creates topic 1)
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic one message for E2E");
        await chatInput.PressAsync("Enter");

        // Wait for response
        await page.Locator(".message-content").First.WaitForAsync(new() { Timeout = 60_000 });

        // Create new topic
        await page.Locator(".new-topic-btn").ClickAsync();

        // Send second message (creates topic 2)
        await Assertions.Expect(chatInput).ToBeEnabledAsync(new() { Timeout = 5_000 });
        await chatInput.FillAsync("Topic two message for E2E");
        await chatInput.PressAsync("Enter");

        // Wait for response in topic 2
        await page.Locator(".message-content").First.WaitForAsync(new() { Timeout = 60_000 });

        // Click topic 1 in sidebar
        var topics = page.Locator(".topic-item");
        (await topics.CountAsync()).ShouldBeGreaterThanOrEqualTo(2);

        // The first topic should be the most recent (topic 2), second should be topic 1
        await topics.Last.ClickAsync();

        // Verify topic 1's messages are shown
        var messageContent = page.Locator(".message-content", new() { HasTextString = "Topic one message for E2E" });
        await messageContent.WaitForAsync(new() { Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task DeleteTopic_RemovesFromSidebar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page);

        // Send a message to create a topic
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic to delete in E2E test");
        await chatInput.PressAsync("Enter");

        // Wait for the topic to appear in sidebar
        var topicItem = page.Locator(".topic-item");
        await topicItem.First.WaitForAsync(new() { Timeout = 30_000 });

        var initialCount = await topicItem.CountAsync();

        // Click delete button on the topic (shows confirm)
        await topicItem.First.Locator(".delete-btn").ClickAsync();

        // Confirm deletion
        await page.Locator(".confirm-delete-btn").ClickAsync();

        // Topic count should decrease
        await page.WaitForFunctionAsync(
            $"() => document.querySelectorAll('.topic-item').length < {initialCount}",
            null,
            new() { Timeout = 10_000 });
    }
}
```

- [ ] **Step 2: Run tests and fix selectors**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatTopicManagementE2ETests" -v n --timeout 600000
```

Key things to check:
- The `.new-topic-btn` selector matches `TopicList.razor`'s new topic button
- The `.delete-btn` and `.confirm-delete-btn` selectors match the delete flow in `TopicList.razor`
- Topic ordering — topics are ordered by `LastMessageAt` descending

- [ ] **Step 3: Verify tests pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WebChatTopicManagementE2ETests" -v n --timeout 600000
```

Expected: Both tests PASS.

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/WebChat/WebChatTopicManagementE2ETests.cs Tests/E2E/WebChat/WebChatE2ETests.cs
git commit -m "feat: add WebChat Topic Management E2E tests (select topic, delete topic)"
```

---

## Task 15: Run full E2E test suite and fix any issues

**Files:**
- Potentially modify any test file from prior tasks

- [ ] **Step 1: Run all E2E tests**

```bash
dotnet test Tests/Tests.csproj --filter "Category=E2E" -v n --timeout 600000
```

- [ ] **Step 2: Fix any failures**

Common issues to watch for:
- Container startup race conditions — add health check waits
- CSS selectors that work in one test but not another due to state leakage (each test gets a fresh page but shares containers)
- Timeout issues — increase timeouts for slow CI environments
- Agent not responding — check agent container logs

- [ ] **Step 3: Verify all tests pass**

```bash
dotnet test Tests/Tests.csproj --filter "Category=E2E" -v n --timeout 600000
```

Expected: All E2E tests PASS (or SKIP if API key unavailable).

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "fix: resolve E2E test suite issues from integration run"
```

---

## Summary

| Task | What | Files |
|------|------|-------|
| 1 | Observability Dockerfile side fix | `Observability/Dockerfile` |
| 2 | Add Playwright package reference | `Tests/Tests.csproj` |
| 3 | Extract FindSolutionRoot to shared utility | `Tests/E2E/Fixtures/TestHelpers.cs` |
| 4 | E2EFixtureBase — Playwright lifecycle | `Tests/E2E/Fixtures/E2EFixtureBase.cs` |
| 5 | DashboardE2EFixture — Redis + Observability | `Tests/E2E/Fixtures/DashboardE2EFixture.cs` |
| 6 | Dashboard Overview tests | `Tests/E2E/Dashboard/DashboardOverviewE2ETests.cs` |
| 7 | Dashboard Navigation tests | `Tests/E2E/Dashboard/DashboardNavigationE2ETests.cs` |
| 8 | Dashboard Real-Time tests | `Tests/E2E/Dashboard/DashboardRealTimeE2ETests.cs` |
| 9 | WebChatE2EFixture — full chat stack | `Tests/E2E/Fixtures/WebChatE2EFixture.cs` |
| 10 | WebChat page load, user selection, connection | `Tests/E2E/WebChat/WebChatE2ETests.cs` |
| 11 | WebChat send message, topic creation | `Tests/E2E/WebChat/WebChatE2ETests.cs` |
| 12 | WebChat cancel streaming | `Tests/E2E/WebChat/WebChatE2ETests.cs` |
| 13 | WebChat approval modal flows | `Tests/E2E/WebChat/WebChatE2ETests.cs` |
| 14 | WebChat topic management | `Tests/E2E/WebChat/WebChatTopicManagementE2ETests.cs` |
| 15 | Full suite integration run | All test files |
