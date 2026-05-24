# Single-Source Agent Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Agent/appsettings.json` the single source of truth for the agent catalog (`Id`, `Name`, `Description`) by having the Agent push its catalog to channel servers via a probed `register_agents` tool, eliminating the duplicated `Agents` config in `McpServerScheduling` and `McpChannelSignalR`.

**Architecture:** A new canonical `AgentCatalogEntry` DTO plus a thread-safe `MutableAgentCatalog`/`IAgentCatalog` live in `Domain`. Both the Scheduling and SignalR MCP servers register `MutableAgentCatalog` as a singleton and expose a `register_agents` tool that calls `catalog.Replace(...)`. The Agent's `ChannelConnectionHost` calls `RegisterAgentsAsync` on every channel connect/reconnect (probe-and-skip when the tool is absent, exactly like `create_conversation`). SignalR additionally broadcasts an `OnAgentsUpdated` hub event so the WebChat selector refreshes live.

**Tech Stack:** .NET 10 / C# 14, ModelContextProtocol 1.2.0 (`AddMcpServer`/`WithTools`), SignalR, Blazor WebAssembly (Redux-like store), xUnit `[Fact]` + Shouldly + Moq, Testcontainers (`redis/redis-stack:latest`).

---

## Conventions (apply to EVERY task)

- **TDD:** Write the failing test first, run it to confirm it fails for the right reason, then implement, then confirm green. RED output is required before GREEN.
- **Assertions:** Shouldly (`.ShouldBe(...)`), never xUnit `Assert.*`. Test framework is xUnit (`[Fact]`).
- **Test naming:** `{Method}_{Scenario}_{ExpectedResult}`.
- **No trailing newline** in any `.cs` file (including tests).
- **MCP tool wrappers carry NO try/catch** — the global `AddCallToolFilter` in each server's `ConfigModule` handles errors.
- **Build:** `dotnet build agent.sln` (must end with `0 Warning(s)` / `0 Error(s)`).
- **Targeted unit tests:** `dotnet test --filter "FullyQualifiedName~<ClassName>"`.
- **Integration tests** require Docker (Testcontainers). In this WSL env ~148 non-E2E failures are the pre-existing `DockerUnavailableException` baseline — ignore those; only the tests named in each task must pass.
- **Commit after each task** (CLAUDE.md auto-commit rule). Branch: `schedule-filesystem-refactor` (do NOT commit to master — this builds on code that only exists on this branch).

---

## File Structure

**Created:**
- `Domain/DTOs/Channel/AgentCatalogEntry.cs` — canonical catalog DTO (replaces `ScheduleAgentInfo` + `WebChat/AgentInfo`).
- `Domain/Contracts/IAgentCatalog.cs` — catalog contract with `Replace` (replaces `IScheduleAgentCatalog`).
- `Domain/Agents/MutableAgentCatalog.cs` — thread-safe in-memory catalog (replaces `ScheduleAgentCatalog`).
- `McpServerScheduling/McpTools/RegisterAgentsTool.cs` — `register_agents` tool.
- `McpChannelSignalR/McpTools/RegisterAgentsTool.cs` — `register_agents` tool + `OnAgentsUpdated` broadcast.
- `Tests/Unit/Domain/Agents/MutableAgentCatalogTests.cs`
- `Tests/Unit/McpChannelSignalR/RegisterAgentsToolTests.cs`
- `Tests/Integration/McpServerTests/McpChannelConnectionRegistrationTests.cs`

**Deleted:**
- `Domain/DTOs/ScheduleAgentInfo.cs`
- `Domain/Contracts/IScheduleAgentCatalog.cs`
- `McpServerScheduling/Services/ScheduleAgentCatalog.cs`
- `Domain/DTOs/WebChat/AgentInfo.cs`
- `Tests/Unit/McpServerScheduling/ScheduleAgentCatalogTests.cs`
- `Tests/Unit/Domain/Scheduling/Vfs/FakeAgentCatalog.cs`

**Modified:** `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs`, `McpServerScheduling/Modules/ConfigModule.cs`, `McpServerScheduling/Settings/SchedulingSettings.cs`, `McpServerScheduling/appsettings.json`, `Tests/Integration/Fixtures/McpSchedulingServerFixture.cs`, `Tests/Integration/McpServerTests/McpSchedulingServerTests.cs`, the scheduling VFS unit tests, `McpChannelSignalR/Hubs/ChatHub.cs`, `McpChannelSignalR/Settings/ChannelSettings.cs`, `McpChannelSignalR/appsettings.json`, `McpChannelSignalR/Modules/ConfigModule.cs`, `Infrastructure/Clients/Channels/IMcpChannelConnection.cs`, `Infrastructure/Clients/Channels/McpChannelConnection.cs`, `Agent/App/ChannelConnectionHost.cs`, `Agent/Modules/InjectorModule.cs`, `Agent/appsettings.json`, `Agent/Program.cs`, and the WebChat client files listed in Task 7.

---

## Task 1: Domain catalog foundation

Create the shared DTO, contract, and thread-safe implementation. Purely additive — `ScheduleAgentInfo`/`AgentInfo` still exist after this task.

**Files:**
- Create: `Domain/DTOs/Channel/AgentCatalogEntry.cs`
- Create: `Domain/Contracts/IAgentCatalog.cs`
- Create: `Domain/Agents/MutableAgentCatalog.cs`
- Test: `Tests/Unit/Domain/Agents/MutableAgentCatalogTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Domain/Agents/MutableAgentCatalogTests.cs`:

```csharp
using Domain.Agents;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

public class MutableAgentCatalogTests
{
    [Fact]
    public void GetAll_BeforeReplace_ReturnsEmpty()
    {
        var catalog = new MutableAgentCatalog();

        catalog.GetAll().ShouldBeEmpty();
        catalog.Exists("jonas").ShouldBeFalse();
        catalog.Get("jonas").ShouldBeNull();
    }

    [Fact]
    public void Replace_ThenQuery_ReflectsNewAgents()
    {
        var catalog = new MutableAgentCatalog();

        catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", "general")]);

        catalog.GetAll().ShouldHaveSingleItem();
        catalog.Exists("jonas").ShouldBeTrue();
        catalog.Exists("ghost").ShouldBeFalse();
        catalog.Get("jonas")!.Name.ShouldBe("Jonas");
    }

    [Fact]
    public void Replace_CalledTwice_DiscardsPreviousAgents()
    {
        var catalog = new MutableAgentCatalog();

        catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", null)]);
        catalog.Replace([new AgentCatalogEntry("jack", "Jack", null)]);

        catalog.Exists("jonas").ShouldBeFalse();
        catalog.Exists("jack").ShouldBeTrue();
    }

    [Fact]
    public void Replace_SnapshotsInput_LaterMutationOfSourceDoesNotLeak()
    {
        var catalog = new MutableAgentCatalog();
        var source = new List<AgentCatalogEntry> { new("jonas", "Jonas", null) };

        catalog.Replace(source);
        source.Add(new AgentCatalogEntry("jack", "Jack", null));

        catalog.GetAll().ShouldHaveSingleItem();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MutableAgentCatalogTests"`
Expected: FAIL — compilation error, `AgentCatalogEntry`/`MutableAgentCatalog` do not exist.

- [ ] **Step 3: Create the DTO**

Create `Domain/DTOs/Channel/AgentCatalogEntry.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record AgentCatalogEntry(string Id, string Name, string? Description);
```

- [ ] **Step 4: Create the contract**

Create `Domain/Contracts/IAgentCatalog.cs`:

```csharp
using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IAgentCatalog
{
    IReadOnlyList<AgentCatalogEntry> GetAll();
    AgentCatalogEntry? Get(string agentId);
    bool Exists(string agentId);
    void Replace(IReadOnlyList<AgentCatalogEntry> agents);
}
```

- [ ] **Step 5: Create the implementation**

Create `Domain/Agents/MutableAgentCatalog.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Domain.Agents;

public sealed class MutableAgentCatalog : IAgentCatalog
{
    private volatile IReadOnlyList<AgentCatalogEntry> _agents = [];

    public IReadOnlyList<AgentCatalogEntry> GetAll() => _agents;

    public AgentCatalogEntry? Get(string agentId) => _agents.FirstOrDefault(a => a.Id == agentId);

    public bool Exists(string agentId) => _agents.Any(a => a.Id == agentId);

    public void Replace(IReadOnlyList<AgentCatalogEntry> agents) => _agents = [.. agents];
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MutableAgentCatalogTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Build**

Run: `dotnet build agent.sln`
Expected: `0 Error(s)`.

- [ ] **Step 8: Commit**

```bash
git add Domain/DTOs/Channel/AgentCatalogEntry.cs Domain/Contracts/IAgentCatalog.cs Domain/Agents/MutableAgentCatalog.cs Tests/Unit/Domain/Agents/MutableAgentCatalogTests.cs
git commit -m "feat(domain): add AgentCatalogEntry + mutable IAgentCatalog"
```

---

## Task 2: Migrate scheduling to the shared catalog

Retype `ScheduleFileSystem` to `IAgentCatalog`, delete the scheduling-specific catalog types, and remove the duplicated `Agents` config. Behavior of `agent_info.json` and validation is unchanged.

**Files:**
- Modify: `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs:13`
- Modify: `McpServerScheduling/Modules/ConfigModule.cs:41`
- Modify: `McpServerScheduling/Settings/SchedulingSettings.cs`
- Modify: `McpServerScheduling/appsettings.json`
- Modify: `Tests/Integration/Fixtures/McpSchedulingServerFixture.cs`
- Modify: scheduling VFS unit tests (see Step 5)
- Delete: `Domain/DTOs/ScheduleAgentInfo.cs`, `Domain/Contracts/IScheduleAgentCatalog.cs`, `McpServerScheduling/Services/ScheduleAgentCatalog.cs`, `Tests/Unit/McpServerScheduling/ScheduleAgentCatalogTests.cs`, `Tests/Unit/Domain/Scheduling/Vfs/FakeAgentCatalog.cs`

- [ ] **Step 1: Retype ScheduleFileSystem to use IAgentCatalog**

In `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs`, change the constructor parameter type (line 13) from:

```csharp
    IScheduleAgentCatalog agents,
```

to:

```csharp
    IAgentCatalog agents,
```

No other change is needed in this file: `agents.GetAll()`, `agents.Get(...)`, and `agents.Exists(...)` are all on `IAgentCatalog`, and the `info` value at line 70 is serialized (not member-accessed), so swapping `ScheduleAgentInfo` for `AgentCatalogEntry` is transparent.

- [ ] **Step 2: Delete the obsolete scheduling catalog types and tests**

```bash
git rm Domain/DTOs/ScheduleAgentInfo.cs Domain/Contracts/IScheduleAgentCatalog.cs McpServerScheduling/Services/ScheduleAgentCatalog.cs Tests/Unit/McpServerScheduling/ScheduleAgentCatalogTests.cs Tests/Unit/Domain/Scheduling/Vfs/FakeAgentCatalog.cs
```

(`MutableAgentCatalog` from Task 1 replaces `ScheduleAgentCatalog`; tests now use `MutableAgentCatalog` directly instead of `FakeAgentCatalog`. The `ScheduleAgentCatalogTests` behavior is covered by `MutableAgentCatalogTests`.)

- [ ] **Step 3: Update scheduling DI registration**

In `McpServerScheduling/Modules/ConfigModule.cs`, change line 41 from:

```csharp
            .AddSingleton<IScheduleAgentCatalog, ScheduleAgentCatalog>()
```

to:

```csharp
            .AddSingleton<IAgentCatalog, MutableAgentCatalog>()
```

Add `using Domain.Agents;` to the using block (for `MutableAgentCatalog`). `Domain.Contracts` is already imported.

- [ ] **Step 4: Remove the Agents config from settings + appsettings**

In `McpServerScheduling/Settings/SchedulingSettings.cs`, delete the `Agents` property and the entire `SchedulingAgentConfig` record. The file becomes:

```csharp
namespace McpServerScheduling.Settings;

public record SchedulingSettings
{
    public required string RedisConnectionString { get; init; }
    public int DispatchIntervalSeconds { get; init; } = 30;
    public IReadOnlyList<string> DefaultDeliverTo { get; init; } = [];
}
```

In `McpServerScheduling/appsettings.json`, delete the `"Agents"` block so the file becomes:

```json
{
  "RedisConnectionString": "redis:6379",
  "DispatchIntervalSeconds": 30,
  "DefaultDeliverTo": [ "signalr" ]
}
```

- [ ] **Step 5: Update scheduling VFS unit tests to use MutableAgentCatalog**

Exactly these five files construct `FakeAgentCatalog`/`ScheduleAgentInfo`: `ScheduleFileSystemReadTests.cs`, `ScheduleFileSystemWriteTests.cs`, `ScheduleFileSystemSearchTests.cs`, `ScheduleFileSystemExecTests.cs`, `ScheduleFileSystemBackendTests.cs`. In each, replace `FakeAgentCatalog` with a `MutableAgentCatalog` seeded via `Replace`, and replace `ScheduleAgentInfo` with `AgentCatalogEntry`. Pattern — wherever a test builds the catalog, change:

```csharp
// before
IScheduleAgentCatalog catalog = new FakeAgentCatalog([new ScheduleAgentInfo("jonas", "Jonas", "general")]);
```

to:

```csharp
// after
var catalog = new MutableAgentCatalog();
catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", "general")]);
```

If a file constructs the fake **inline** inside another expression (e.g. `new ScheduleFileSystem(store, new FakeAgentCatalog([...]), validator)`), first hoist it to a local `catalog` variable (two lines as shown above), then pass `catalog` into that expression.

Update usings in each affected test file: remove `using Domain.DTOs;` if it was only for `ScheduleAgentInfo`; add `using Domain.Agents;` and `using Domain.DTOs.Channel;`.

> Implementer note: do NOT touch `SchedulePathTests.cs` — its `AgentInfoFile` / `AgentInfoFileName` references are the path enum/constant, unrelated to the deleted DTO. After editing the five files, no reference to `FakeAgentCatalog` or `ScheduleAgentInfo` may remain anywhere.

- [ ] **Step 6: Seed the integration fixture catalog via the service**

In `Tests/Integration/Fixtures/McpSchedulingServerFixture.cs`, remove the `Agents` initializer from the `SchedulingSettings` object construction (it no longer exists), then seed the catalog through DI after the host starts. Replace the settings construction + start block so it reads:

```csharp
        var settings = new SchedulingSettings
        {
            RedisConnectionString = redisConnection,
            DispatchIntervalSeconds = 3600,
            DefaultDeliverTo = ["signalr"]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureScheduling(settings);

        var app = builder.Build();
        app.MapMcp("/mcp");

        app.Services.GetRequiredService<IAgentCatalog>()
            .Replace([new AgentCatalogEntry("jonas", "Jonas", "test agent")]);

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{port}/mcp";
```

Add usings: `using Domain.Contracts;`, `using Domain.DTOs.Channel;`, `using Microsoft.Extensions.DependencyInjection;`.

- [ ] **Step 7: Run scheduling tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~Scheduling.Vfs"`
Expected: PASS (all scheduling VFS unit tests).

Run (Docker required): `dotnet test --filter "FullyQualifiedName~McpSchedulingServerTests"`
Expected: PASS (6 tests, including the `CreateGlobRead_RoundTrip` which depends on the seeded `jonas`).

- [ ] **Step 8: Build**

Run: `dotnet build agent.sln`
Expected: `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor(scheduling): use shared IAgentCatalog, drop duplicated Agents config"
```

---

## Task 3: Add the scheduling `register_agents` tool

Expose `register_agents` on the scheduling server so the Agent can populate the catalog at runtime.

**Files:**
- Create: `McpServerScheduling/McpTools/RegisterAgentsTool.cs`
- Modify: `McpServerScheduling/Modules/ConfigModule.cs`
- Test: `Tests/Integration/McpServerTests/McpSchedulingServerTests.cs`

- [ ] **Step 1: Write the failing integration test**

Add these two tests to `Tests/Integration/McpServerTests/McpSchedulingServerTests.cs` (inside the existing class):

```csharp
    [Fact]
    public async Task McpServer_ListTools_IncludesRegisterAgents()
    {
        var client = await ConnectAsync();

        var toolNames = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        toolNames.ShouldContain("register_agents");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task RegisterAgents_ThenCreateForNewAgent_PassesValidation()
    {
        var client = await ConnectAsync();

        var register = await client.CallToolAsync(
            "register_agents",
            new Dictionary<string, object?>
            {
                ["agents"] =
                    """[{"id":"jonas","name":"Jonas","description":"general"},{"id":"jack","name":"Jack","description":"downloads"}]"""
            },
            cancellationToken: CancellationToken.None);

        (register.IsError ?? false).ShouldBeFalse();

        var create = await client.CallToolAsync(
            "fs_create",
            new Dictionary<string, object?>
            {
                ["path"] = "/jack/itest-register/schedule.json",
                ["content"] = """{"prompt":"do the thing","cron":"0 7 * * *"}"""
            },
            cancellationToken: CancellationToken.None);

        (create.IsError ?? false).ShouldBeFalse();

        var info = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?> { ["path"] = "/jack/agent_info.json" },
            cancellationToken: CancellationToken.None);

        info.Content.OfType<TextContentBlock>().First().Text.ShouldContain("Jack");

        await client.DisposeAsync();
    }
```

(Registering `[jonas, jack]` keeps `jonas` present, so the other tests in this class remain order-independent.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~McpSchedulingServerTests"`
Expected: FAIL — `register_agents` tool not found (`McpServer_ListTools_IncludesRegisterAgents` fails; `RegisterAgents_ThenCreateForNewAgent_PassesValidation` fails calling an unknown tool).

- [ ] **Step 3: Create the tool**

Create `McpServerScheduling/McpTools/RegisterAgentsTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IAgentCatalog catalog)
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "register_agents")]
    [Description("Register the set of agents that schedules may target (replaces any previously registered set)")]
    public string McpRun([Description("JSON array of {id, name, description}")] string agents)
    {
        var entries = JsonSerializer.Deserialize<List<AgentCatalogEntry>>(agents, _options) ?? [];
        catalog.Replace(entries);
        return $"registered {entries.Count} agents";
    }
}
```

- [ ] **Step 4: Register the tool**

In `McpServerScheduling/Modules/ConfigModule.cs`, add `.WithTools<RegisterAgentsTool>()` to the `AddMcpServer()` chain (next to the other `.WithTools<...>()` calls, e.g. immediately after `.WithTools<RequestApprovalTool>()`):

```csharp
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<FsGlobTool>()
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~McpSchedulingServerTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Build**

Run: `dotnet build agent.sln`
Expected: `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 7: Commit**

```bash
git add McpServerScheduling/McpTools/RegisterAgentsTool.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Integration/McpServerTests/McpSchedulingServerTests.cs
git commit -m "feat(scheduling): add register_agents tool to populate the catalog at runtime"
```

---

## Task 4: SignalR catalog + `register_agents` tool + broadcast

Replace SignalR's static `Agents` config with the shared catalog, add the `register_agents` tool (which also broadcasts `OnAgentsUpdated`), and migrate `ChatHub` to read from the catalog.

**Files:**
- Create: `McpChannelSignalR/McpTools/RegisterAgentsTool.cs`
- Modify: `McpChannelSignalR/Hubs/ChatHub.cs`
- Modify: `McpChannelSignalR/Settings/ChannelSettings.cs`
- Modify: `McpChannelSignalR/appsettings.json`
- Modify: `McpChannelSignalR/Modules/ConfigModule.cs`
- Test: `Tests/Unit/McpChannelSignalR/RegisterAgentsToolTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelSignalR/RegisterAgentsToolTests.cs`:

```csharp
using Domain.Contracts;
using McpChannelSignalR.McpTools;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class RegisterAgentsToolTests
{
    [Fact]
    public void McpRun_ReplacesCatalog_AndBroadcastsUpdate()
    {
        var catalog = new Domain.Agents.MutableAgentCatalog();
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun(
            """[{"id":"jonas","name":"Jonas","description":"general"}]""");

        result.ShouldBe("registered 1 agents");
        catalog.Exists("jonas").ShouldBeTrue();
        sender.Verify(
            s => s.SendAsync("OnAgentsUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void McpRun_WithEmptyArray_ClearsCatalog()
    {
        var catalog = new Domain.Agents.MutableAgentCatalog();
        catalog.Replace([new Domain.DTOs.Channel.AgentCatalogEntry("old", "Old", null)]);
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun("[]");

        result.ShouldBe("registered 0 agents");
        catalog.GetAll().ShouldBeEmpty();
    }
}
```

Confirm `IHubNotificationSender.SendAsync` signature is `Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default)` (it is — see `Domain/Contracts/IHubNotificationSender.cs` / `SignalRHubNotificationSender`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RegisterAgentsToolTests"`
Expected: FAIL — `McpChannelSignalR.McpTools.RegisterAgentsTool` does not exist.

- [ ] **Step 3: Create the tool**

Create `McpChannelSignalR/McpTools/RegisterAgentsTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IAgentCatalog catalog, IHubNotificationSender hubSender)
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "register_agents")]
    [Description("Register the agents available to WebChat (replaces any previously registered set)")]
    public string McpRun([Description("JSON array of {id, name, description}")] string agents)
    {
        var entries = JsonSerializer.Deserialize<List<AgentCatalogEntry>>(agents, _options) ?? [];
        catalog.Replace(entries);
        _ = hubSender.SendAsync("OnAgentsUpdated", entries);
        return $"registered {entries.Count} agents";
    }
}
```

(Broadcast is fire-and-forget so a slow/failed client push never blocks registration; `entries` is the payload WebChat consumes in Task 7.)

- [ ] **Step 4: Run the tool test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RegisterAgentsToolTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Add the catalog singleton and register the tool**

In `McpChannelSignalR/Modules/ConfigModule.cs`:

Add usings: `using Domain.Agents;` and (if not present) `using Domain.Contracts;` (already present).

Register the catalog singleton — add to the first `services` chain (e.g., after `.AddSingleton(settings)`):

```csharp
            .AddSingleton<IAgentCatalog, MutableAgentCatalog>()
```

Register the tool — add to the `AddMcpServer()` chain after `.WithTools<CreateConversationTool>()`:

```csharp
            .WithTools<CreateConversationTool>()
            .WithTools<RegisterAgentsTool>()
```

- [ ] **Step 6: Migrate ChatHub to read from the catalog**

In `McpChannelSignalR/Hubs/ChatHub.cs`:

Replace the constructor parameter `ChannelSettings settings` with `IAgentCatalog catalog`. The constructor becomes:

```csharp
public sealed class ChatHub(
    SessionService sessionService,
    StreamService streamService,
    ApprovalService approvalService,
    ChannelNotificationEmitter notificationEmitter,
    IAgentCatalog catalog,
    RedisStateService redisStateService,
    IPushSubscriptionStore pushSubscriptionStore) : Hub
```

Replace `GetAgents()` (lines 45-50) with:

```csharp
    public IReadOnlyList<AgentCatalogEntry> GetAgents()
    {
        return catalog.GetAll();
    }
```

Replace `ValidateAgent(...)` (lines 52-55) with:

```csharp
    public bool ValidateAgent(string agentId)
    {
        return catalog.Exists(agentId);
    }
```

Update usings: add `using Domain.DTOs.Channel;` (for `AgentCatalogEntry`). Keep `using Domain.DTOs.WebChat;` (other DTOs), keep `using McpChannelSignalR.Settings;` (`SpaceConfig`). `using Domain.Contracts;` is already present (for `IAgentCatalog`).

- [ ] **Step 7: Remove the Agents config from settings + appsettings**

In `McpChannelSignalR/Settings/ChannelSettings.cs`, delete the `Agents` property and the entire `AgentConfig` record. The file becomes:

```csharp
namespace McpChannelSignalR.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
    public WebPushConfig? WebPush { get; init; }
}

public record WebPushConfig
{
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Subject { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);
}
```

In `McpChannelSignalR/appsettings.json`, delete the `"Agents"` block so the file becomes:

```json
{
  "RedisConnectionString": "redis:6379",
  "WebPush": {
    "PublicKey": "",
    "Subject": "",
    "PrivateKey": ""
  }
}
```

- [ ] **Step 8: Build and run the tool test**

Run: `dotnet build agent.sln`
Expected: `0 Error(s)`, `0 Warning(s)`.

Run: `dotnet test --filter "FullyQualifiedName~RegisterAgentsToolTests"`
Expected: PASS (2 tests).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat(signalr): catalog-backed agents + register_agents tool with OnAgentsUpdated broadcast"
```

---

## Task 5: Agent-side registration (client + host wiring)

Add `RegisterAgentsAsync` to the channel connection (probe-and-call), call it from `ChannelConnectionHost` after connect/reconnect, project the catalog from settings, and fill in the agent descriptions in `Agent/appsettings.json`.

**Files:**
- Modify: `Infrastructure/Clients/Channels/IMcpChannelConnection.cs`
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs`
- Modify: `Agent/App/ChannelConnectionHost.cs`
- Modify: `Agent/Modules/InjectorModule.cs`
- Modify: `Agent/appsettings.json`
- Modify: `Tests/Unit/Infrastructure/ChannelConnectionHostTests.cs` (extend `FakeMcpChannelConnection` + add a test; fix existing constructor calls)
- Create: `Tests/Integration/McpServerTests/McpChannelConnectionRegistrationTests.cs`

- [ ] **Step 1: Write the failing host test (and extend the fake)**

In `Tests/Unit/Infrastructure/ChannelConnectionHostTests.cs`:

(a) Extend `FakeMcpChannelConnection` to implement the new interface member and expose what was registered. Add these members inside the class:

```csharp
    private readonly TaskCompletionSource _firstRegister = new();
    public IReadOnlyList<AgentCatalogEntry>? RegisteredAgents { get; private set; }

    public Task RegisterAgentsAsync(IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct)
    {
        RegisteredAgents = agents;
        _firstRegister.TrySetResult();
        return Task.CompletedTask;
    }

    public Task WaitForRegisterAsync(CancellationToken ct) => _firstRegister.Task.WaitAsync(ct);
```

Add `using Domain.DTOs.Channel;` to the test file.

(b) Fix the four existing `new ChannelConnectionHost(...)` calls to pass an empty catalog as the new third argument:
- `new ChannelConnectionHost(endpoints, [fake], _logger)` → `new ChannelConnectionHost(endpoints, [fake], [], _logger)`
- `new ChannelConnectionHost(endpoints, [fake], _logger, healthCheckInterval: ...)` → `new ChannelConnectionHost(endpoints, [fake], [], _logger, healthCheckInterval: ...)` (two occurrences)
- the `RetriesConnectionOnInitialFailure` one likewise gets `[]` as the third arg.

(c) Add the new test:

```csharp
    [Fact]
    public async Task RegistersAgents_AfterConnect()
    {
        var fake = new FakeMcpChannelConnection("ch-1");
        var catalog = new[] { new AgentCatalogEntry("jonas", "Jonas", "general") };
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9999" } };
        var sut = new ChannelConnectionHost(endpoints, [fake], catalog, _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _ = sut.StartAsync(cts.Token);

        await fake.WaitForRegisterAsync(cts.Token);
        fake.RegisteredAgents.ShouldNotBeNull();
        fake.RegisteredAgents!.Single().Id.ShouldBe("jonas");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ChannelConnectionHostTests"`
Expected: FAIL — `IMcpChannelConnection` has no `RegisterAgentsAsync` (fake won't compile against the interface) and `ChannelConnectionHost` has no catalog parameter.

- [ ] **Step 3: Add RegisterAgentsAsync to the interface**

In `Infrastructure/Clients/Channels/IMcpChannelConnection.cs`:

```csharp
using Domain.DTOs.Channel;

namespace Infrastructure.Clients.Channels;

public interface IMcpChannelConnection
{
    string ChannelId { get; }
    Task ConnectAsync(string endpoint, CancellationToken ct);
    Task<bool> IsHealthyAsync(CancellationToken ct);
    Task ReconnectAsync(string endpoint, CancellationToken ct);
    Task RegisterAgentsAsync(IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct);
}
```

- [ ] **Step 4: Implement RegisterAgentsAsync (probe-and-call)**

In `Infrastructure/Clients/Channels/McpChannelConnection.cs`, add this method (mirrors `CreateConversationAsync`). Place it after `CreateConversationAsync`:

```csharp
    public async Task RegisterAgentsAsync(IReadOnlyList<AgentCatalogEntry> agents, CancellationToken ct)
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            var tools = await _client.ListToolsAsync(cancellationToken: ct);
            if (tools.All(t => t.Name != "register_agents"))
            {
                return;
            }

            await _client.CallToolAsync(
                "register_agents",
                new Dictionary<string, object?>
                {
                    ["agents"] = JsonSerializer.Serialize(agents)
                },
                cancellationToken: ct);
        }
        catch (McpException)
        {
        }
    }
```

(`using Domain.DTOs.Channel;`, `using System.Text.Json;`, and `using ModelContextProtocol;` are already present in this file.)

- [ ] **Step 5: Wire the host to register after connect/reconnect**

In `Agent/App/ChannelConnectionHost.cs`:

Add `using Domain.DTOs.Channel;`.

Add the catalog constructor parameter (before `logger` so the optional `healthCheckInterval` stays last):

```csharp
public class ChannelConnectionHost(
    ChannelEndpoint[] endpoints,
    IReadOnlyList<IMcpChannelConnection> connections,
    IReadOnlyList<AgentCatalogEntry> agentCatalog,
    ILogger<ChannelConnectionHost> logger,
    TimeSpan? healthCheckInterval = null) : BackgroundService
```

In `MaintainConnectionAsync`, register after the initial connect and after each reconnect:

```csharp
    private async Task MaintainConnectionAsync(
        IMcpChannelConnection conn, string endpoint, CancellationToken ct)
    {
        await ConnectWithRetryAsync(conn, endpoint, ct);
        await RegisterAgentsSafelyAsync(conn, ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_healthCheckInterval, ct);

            if (!await conn.IsHealthyAsync(ct))
            {
                logger.LogWarning("Channel {ChannelId} health check failed, reconnecting", conn.ChannelId);
                await ReconnectWithRetryAsync(conn, endpoint, ct);
                await RegisterAgentsSafelyAsync(conn, ct);
            }
        }
    }

    private async Task RegisterAgentsSafelyAsync(IMcpChannelConnection conn, CancellationToken ct)
    {
        try
        {
            await conn.RegisterAgentsAsync(agentCatalog, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Failed to register agents with channel {ChannelId}: {Error}", conn.ChannelId, ex.Message);
        }
    }
```

- [ ] **Step 6: Project the catalog in InjectorModule**

In `Agent/Modules/InjectorModule.cs`, add `using Domain.DTOs.Channel;`, then pass the projected catalog into the host registration (line ~68):

```csharp
                .AddHostedService(sp =>
                    new ChannelConnectionHost(
                        settings.ChannelEndpoints,
                        sp.GetServices<IChannelConnection>().OfType<IMcpChannelConnection>().ToList(),
                        settings.Agents.Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description)).ToList(),
                        sp.GetRequiredService<ILogger<ChannelConnectionHost>>()));
```

- [ ] **Step 7: Fill in agent descriptions (preserve what the removed configs held)**

In `Agent/appsettings.json`, add a `"description"` to each agent so the catalog is meaningful (these descriptions previously lived in the now-deleted scheduling/SignalR configs). Add the field to the `jack` and `jonas` objects:

For `jack` (after `"name": "Jack",`):
```json
            "description": "Download assistant (library/torrent search and downloads).",
```

For `jonas` (after `"name": "Jonas",`):
```json
            "description": "General-purpose personal assistant (vault notes, web search, real-estate, home automation).",
```

- [ ] **Step 8: Run host test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ChannelConnectionHostTests"`
Expected: PASS (5 tests — 4 existing + `RegistersAgents_AfterConnect`).

- [ ] **Step 9: Write the failing connection integration test**

Create `Tests/Integration/McpServerTests/McpChannelConnectionRegistrationTests.cs`:

```csharp
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpChannelConnectionRegistrationTests
{
    public class AgainstScheduling(McpSchedulingServerFixture fixture)
        : IClassFixture<McpSchedulingServerFixture>
    {
        [Fact]
        public async Task RegisterAgentsAsync_PopulatesCatalog_OnServerWithTool()
        {
            var conn = new McpChannelConnection("scheduling");
            await conn.ConnectAsync(fixture.McpEndpoint, CancellationToken.None);

            await conn.RegisterAgentsAsync(
                [
                    new AgentCatalogEntry("jonas", "Jonas", "general"),
                    new AgentCatalogEntry("zeta", "Zeta", "extra")
                ],
                CancellationToken.None);

            var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(fixture.McpEndpoint) }),
                cancellationToken: CancellationToken.None);

            var create = await client.CallToolAsync(
                "fs_create",
                new Dictionary<string, object?>
                {
                    ["path"] = "/zeta/itest-conn/schedule.json",
                    ["content"] = """{"prompt":"hi","cron":"0 6 * * *"}"""
                },
                cancellationToken: CancellationToken.None);

            (create.IsError ?? false).ShouldBeFalse();

            await client.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    public class AgainstServerWithoutTool(McpVaultServerFixture fixture)
        : IClassFixture<McpVaultServerFixture>
    {
        [Fact]
        public async Task RegisterAgentsAsync_OnServerWithoutTool_DoesNotThrow()
        {
            var conn = new McpChannelConnection("vault");
            await conn.ConnectAsync(fixture.McpEndpoint, CancellationToken.None);

            await Should.NotThrowAsync(() =>
                conn.RegisterAgentsAsync([new AgentCatalogEntry("jonas", "Jonas", null)], CancellationToken.None));

            await conn.DisposeAsync();
        }
    }
}
```

> Implementer note: confirm `McpVaultServerFixture` exposes a `McpEndpoint` property (it does). If its constructor shape differs, mirror the pattern used by an existing test that consumes `McpVaultServerFixture`.

- [ ] **Step 10: Run the integration test (Docker required)**

Run: `dotnet test --filter "FullyQualifiedName~McpChannelConnectionRegistrationTests"`
Expected: PASS (2 tests). (Step 4's implementation already satisfies them; if you ran this before Step 4 it would FAIL with "tool not found" / not populate.)

- [ ] **Step 11: Build**

Run: `dotnet build agent.sln`
Expected: `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat(agent): register agent catalog with channels on connect/reconnect"
```

---

## Task 6: Unify the Agent REST DTO

Switch the Agent's `/api/agents` endpoints from `WebChat.AgentInfo` to `AgentCatalogEntry`. (`AgentInfo.cs` itself is deleted in Task 7, after WebChat is migrated.)

**Files:**
- Modify: `Agent/Program.cs`

- [ ] **Step 1: Update Agent/Program.cs**

In `Agent/Program.cs`:

Replace `using Domain.DTOs.WebChat;` with `using Domain.DTOs.Channel;`.

Change line ~26:
```csharp
app.MapGet("/api/agents", (IAgentDefinitionProvider provider, string? userId) =>
    provider.GetAll(userId).Select(a => new AgentCatalogEntry(a.Id, a.Name, a.Description)));
```

Change line ~31:
```csharp
    return new AgentCatalogEntry(definition.Id, definition.Name, definition.Description);
```

- [ ] **Step 2: Build**

Run: `dotnet build agent.sln`
Expected: `0 Error(s)`, `0 Warning(s)`. (Build still green because `AgentInfo` still exists for WebChat.)

- [ ] **Step 3: Commit**

```bash
git add Agent/Program.cs
git commit -m "refactor(agent): /api/agents returns AgentCatalogEntry"
```

---

## Task 7: Unify the WebChat DTO + live agent updates

Rename `AgentInfo` → `AgentCatalogEntry` across the WebChat client, subscribe to `OnAgentsUpdated` to refresh the selector live, then delete `AgentInfo.cs`.

**Files:**
- Modify: `WebChat.Client/_Imports.razor`
- Modify: `WebChat.Client/Contracts/IAgentService.cs`
- Modify: `WebChat.Client/Services/AgentService.cs`
- Modify: `WebChat.Client/State/Topics/TopicsState.cs`
- Modify: `WebChat.Client/State/Topics/TopicsActions.cs`
- Modify: `WebChat.Client/Components/AgentSelector.razor`
- Modify: `WebChat.Client/Components/TopicList.razor`
- Modify: `WebChat.Client/Components/Chat/MessageList.razor`
- Modify: `WebChat.Client/Components/Chat/EmptyState.razor`
- Modify: `WebChat.Client/State/Hub/IHubEventDispatcher.cs`
- Modify: `WebChat.Client/State/Hub/HubEventDispatcher.cs`
- Modify: `WebChat.Client/Services/SignalREventSubscriber.cs`
- Modify: `Tests/Unit/WebChat.Client/State/TopicsStoreTests.cs`
- Delete: `Domain/DTOs/WebChat/AgentInfo.cs`

- [ ] **Step 1: Update the failing test first**

In `Tests/Unit/WebChat.Client/State/TopicsStoreTests.cs`, update the `SetAgents_UpdatesAgentsList` test (line ~177) to use `AgentCatalogEntry`:

```csharp
        var agents = new List<AgentCatalogEntry>
        {
            new("agent-1", "Agent One", null),
            new("agent-2", "Agent Two", "Description")
        };
```

Add `using Domain.DTOs.Channel;` to the test file (and remove `using Domain.DTOs.WebChat;` if it was only for `AgentInfo`).

Add a new test for the live-update dispatcher behavior (append to the same file, or to a `HubEventDispatcherTests` if one exists — if not, add here for locality):

```csharp
    [Fact]
    public void HandleAgentsUpdated_DispatchesSetAgents()
    {
        var agents = new List<AgentCatalogEntry> { new("agent-1", "Agent One", null) };

        _dispatcher.Dispatch(new SetAgents(agents));

        _store.State.Agents.ShouldHaveSingleItem();
        _store.State.Agents[0].Id.ShouldBe("agent-1");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~TopicsStoreTests"`
Expected: FAIL — `AgentCatalogEntry` not usable where `SetAgents`/`TopicsState.Agents` still expect `AgentInfo` (type mismatch), and `using Domain.DTOs.Channel;` resolves to a type not yet wired through state.

- [ ] **Step 3: Add the Channel namespace to WebChat imports**

In `WebChat.Client/_Imports.razor`, add after the existing `@using Domain.DTOs.WebChat`:

```razor
@using Domain.DTOs.Channel
```

- [ ] **Step 4: Rename AgentInfo → AgentCatalogEntry in WebChat state + services**

Apply these edits (add `using Domain.DTOs.Channel;` to each `.cs` file; keep `using Domain.DTOs.WebChat;` where other WebChat DTOs are used):

`WebChat.Client/State/Topics/TopicsState.cs:10`:
```csharp
    public IReadOnlyList<AgentCatalogEntry> Agents { get; init; } = [];
```

`WebChat.Client/State/Topics/TopicsActions.cs:18`:
```csharp
public record SetAgents(IReadOnlyList<AgentCatalogEntry> Agents) : IAction;
```

`WebChat.Client/Contracts/IAgentService.cs`:
```csharp
using Domain.DTOs.Channel;

namespace WebChat.Client.Contracts;

public interface IAgentService
{
    Task<IReadOnlyList<AgentCatalogEntry>> GetAgentsAsync();
}
```

`WebChat.Client/Services/AgentService.cs` — change the return type and the `InvokeAsync<...>` generic:
```csharp
    public async Task<IReadOnlyList<AgentCatalogEntry>> GetAgentsAsync()
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return [];
        }

        return await hubConnection.InvokeAsync<IReadOnlyList<AgentCatalogEntry>>("GetAgents");
    }
```
(add `using Domain.DTOs.Channel;`, remove `using Domain.DTOs.WebChat;` if now unused.)

- [ ] **Step 5: Rename AgentInfo → AgentCatalogEntry in WebChat components**

`WebChat.Client/Components/AgentSelector.razor:2`:
```csharp
    [Parameter] public List<AgentCatalogEntry> Agents { get; set; } = [];
```

`WebChat.Client/Components/Chat/EmptyState.razor:2`:
```csharp
    [Parameter] public AgentCatalogEntry? SelectedAgent { get; set; }
```

`WebChat.Client/Components/Chat/MessageList.razor:16`:
```csharp
    private AgentCatalogEntry? _selectedAgent;
```

`WebChat.Client/Components/TopicList.razor:10`:
```csharp
    private IReadOnlyList<AgentCatalogEntry> _agents = [];
```

(The `@using Domain.DTOs.Channel` added in Step 3 resolves these. If any component has a local `@using Domain.DTOs.WebChat` it can stay.)

- [ ] **Step 6: Add the live-update handler**

`WebChat.Client/State/Hub/IHubEventDispatcher.cs` — add the method and `using Domain.DTOs.Channel;`:
```csharp
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Hub;

public interface IHubEventDispatcher
{
    void HandleTopicChanged(TopicChangedNotification notification);
    void HandleStreamChanged(StreamChangedNotification notification);
    void HandleApprovalResolved(ApprovalResolvedNotification notification);
    void HandleToolCalls(ToolCallsNotification notification);
    void HandleUserMessage(UserMessageNotification notification);
    void HandleAgentsUpdated(IReadOnlyList<AgentCatalogEntry> agents);
}
```

`WebChat.Client/State/Hub/HubEventDispatcher.cs` — add `using Domain.DTOs.Channel;` and implement the method (place near the other handlers):
```csharp
    public void HandleAgentsUpdated(IReadOnlyList<AgentCatalogEntry> agents)
    {
        dispatcher.Dispatch(new SetAgents(agents));
    }
```

`WebChat.Client/Services/SignalREventSubscriber.cs` — add `using Domain.DTOs.Channel;` and register the subscription (inside `Subscribe()`, alongside the others):
```csharp
        _subscriptions.Add(
            hubConnection.On<IReadOnlyList<AgentCatalogEntry>>(
                "OnAgentsUpdated", hubEventDispatcher.HandleAgentsUpdated));
```

- [ ] **Step 7: Delete the obsolete DTO**

```bash
git rm Domain/DTOs/WebChat/AgentInfo.cs
```

- [ ] **Step 8: Run tests + build**

Run: `dotnet test --filter "FullyQualifiedName~TopicsStoreTests"`
Expected: PASS.

Run: `dotnet build agent.sln`
Expected: `0 Error(s)`, `0 Warning(s)`. (No remaining references to `Domain.DTOs.WebChat.AgentInfo` anywhere.)

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor(webchat): unify on AgentCatalogEntry + live OnAgentsUpdated refresh"
```

---

## Final verification

- [ ] **Step 1: Full build**

Run: `dotnet build agent.sln`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 2: Confirm no orphaned references**

Run:
```bash
grep -rn "ScheduleAgentInfo\|IScheduleAgentCatalog\|ScheduleAgentCatalog\|FakeAgentCatalog\|Domain.DTOs.WebChat.AgentInfo" --include=*.cs --include=*.razor . | grep -v "/obj/" | grep -v "/bin/"
```
Expected: no output. Also confirm `grep -rn "\.Agents" McpServerScheduling McpChannelSignalR --include=*.cs` shows no references to a settings `Agents` member.

- [ ] **Step 3: Run the full affected test set (Docker required for integration)**

Run:
```bash
dotnet test --filter "FullyQualifiedName~MutableAgentCatalogTests|FullyQualifiedName~Scheduling.Vfs|FullyQualifiedName~McpSchedulingServerTests|FullyQualifiedName~RegisterAgentsToolTests|FullyQualifiedName~ChannelConnectionHostTests|FullyQualifiedName~McpChannelConnectionRegistrationTests|FullyQualifiedName~TopicsStoreTests"
```
Expected: all PASS (any unrelated `DockerUnavailableException` failures are the known baseline, not regressions).

- [ ] **Step 4: Sanity-check the wire contract end-to-end (manual, optional)**

Bring up `agent`, `mcp-scheduling`, `mcp-channel-signalr` (see CLAUDE.md launch command). Confirm the agent logs show register attempts for the `scheduling` and `signalr` channels at startup, the WebChat agent selector lists Jack/Jonas, and creating a schedule via the VFS validates the agent id.

---

## Spec coverage check

| Spec section | Task(s) |
|---|---|
| §2 `register_agents` standard probed tool | 3 (scheduling), 4 (signalr), 5 (client probe-and-call + host) |
| §3 canonical `AgentCatalogEntry` / `IAgentCatalog` / `MutableAgentCatalog` | 1; replaces `ScheduleAgentInfo` (2) and `WebChat.AgentInfo` (6, 7) |
| §4 scheduling reads catalog; config removed | 2, 3 |
| §5 signalr catalog + broadcast; WebChat refresh; config removed | 4, 7 |
| §6 empty-catalog behavior | inherent (catalog starts empty; validation rejects; selector empty) |
| §7 Telegram/ServiceBus skip the probe | inherent (no `register_agents` tool there; client probe skips) |
| §8 testing | every task is TDD with Shouldly |
