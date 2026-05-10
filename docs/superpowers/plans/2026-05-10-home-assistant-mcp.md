# Home Assistant MCP Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a new `McpServerHomeAssistant` MCP server exposing four generic tools (`home_list_entities`, `home_get_state`, `home_list_services`, `home_call_service`) that proxy to a containerized Home Assistant instance, giving the agent control of every HA-connected device (Roborock vacuum, lights, climate, etc.).

**Architecture:** Domain (`IHomeAssistantClient`, DTOs, four `Home*Tool` base classes, `HomeAssistantPrompt`) → Infrastructure (`HomeAssistantClient` HTTP client with Bearer auth + retry) → `McpServerHomeAssistant` (Program/ConfigModule + four `McpHome*Tool` wrappers + `McpSystemPrompt`) → Docker Compose adds `homeassistant` (LAN-accessible web UI on `:8123`) and `mcp-homeassistant` services. Agent config registers the new MCP endpoint and whitelist pattern; existing `ToolApprovalChatClient` auto-approves all four tools.

**Tech Stack:** .NET 10, C# 14 (`extension(...)` syntax), `ModelContextProtocol.AspNetCore` 1.2.0, `Microsoft.Extensions.Hosting` 10.0.7, Polly retry policy via existing `AddRetryWithExponentialWaitPolicy`, xUnit + WireMock.Net for tests, Home Assistant `ghcr.io/home-assistant/home-assistant:stable`.

**Reference spec:** `docs/superpowers/specs/2026-05-10-home-assistant-mcp-design.md`

---

## File map

### New files

| Path | Responsibility |
|---|---|
| `Domain/Contracts/IHomeAssistantClient.cs` | Client interface + query/result records (entities, services, service-call). |
| `Domain/Exceptions/HomeAssistantException.cs` | Base exception + `HomeAssistantUnauthorizedException` + `HomeAssistantNotFoundException`. |
| `Domain/Prompts/HomeAssistantPrompt.cs` | `Name`, `Description`, `SystemPrompt` constants (matches `IdealistaPrompt`). |
| `Domain/Tools/HomeAssistant/HomeListEntitiesTool.cs` | Pure tool: filter+project entity states. |
| `Domain/Tools/HomeAssistant/HomeGetStateTool.cs` | Pure tool: full state for one entity, 404 → `{ok:false}` envelope. |
| `Domain/Tools/HomeAssistant/HomeListServicesTool.cs` | Pure tool: flatten HA's nested service registry. |
| `Domain/Tools/HomeAssistant/HomeCallServiceTool.cs` | Pure tool: hoist `entity_id` into `target`, return changed entities. |
| `Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs` | HttpClient impl: GET/POST + Bearer header + JSON shape mapping + exception mapping. |
| `Infrastructure/Extensions/HomeAssistantClientExtensions.cs` | `services.AddHomeAssistantClient(settings)` (HttpClient + retry policy). |
| `McpServerHomeAssistant/McpServerHomeAssistant.csproj` | Project file (mirror of `McpServerIdealista.csproj`). |
| `McpServerHomeAssistant/Program.cs` | Standard `WebApplication` + `MapMcp("/mcp")`. |
| `McpServerHomeAssistant/Settings/McpSettings.cs` | `HomeAssistantConfiguration { BaseUrl, Token }`. |
| `McpServerHomeAssistant/Modules/ConfigModule.cs` | DI: settings, HA client, MCP server with global error filter, four tools, prompt. |
| `McpServerHomeAssistant/McpTools/McpHomeListEntitiesTool.cs` | Wrapper with `[McpServerToolType]`/`[McpServerTool]` attributes. |
| `McpServerHomeAssistant/McpTools/McpHomeGetStateTool.cs` | Wrapper. |
| `McpServerHomeAssistant/McpTools/McpHomeListServicesTool.cs` | Wrapper. |
| `McpServerHomeAssistant/McpTools/McpHomeCallServiceTool.cs` | Wrapper. |
| `McpServerHomeAssistant/McpPrompts/McpSystemPrompt.cs` | `[McpServerPromptType]` exposing `HomeAssistantPrompt.SystemPrompt`. |
| `McpServerHomeAssistant/Dockerfile` | Mirror of `McpServerIdealista/Dockerfile` with renamed paths. |
| `McpServerHomeAssistant/appsettings.json` | Placeholder config. |
| `Tests/Unit/Infrastructure/HomeAssistantClientTests.cs` | WireMock-driven tests for the client (request shape + error mapping). |
| `Tests/Unit/Domain/HomeAssistant/HomeListEntitiesToolTests.cs` | Tests via `HomeAssistantClient` against WireMock. |
| `Tests/Unit/Domain/HomeAssistant/HomeGetStateToolTests.cs` | Same. |
| `Tests/Unit/Domain/HomeAssistant/HomeListServicesToolTests.cs` | Same. |
| `Tests/Unit/Domain/HomeAssistant/HomeCallServiceToolTests.cs` | Same — verifies `entity_id` hoisting + changed-entities mapping. |

### Modified files

| Path | Change |
|---|---|
| `agent.sln` | Register `McpServerHomeAssistant` project. |
| `Agent/appsettings.json` | Add `mcp-homeassistant` endpoint + whitelist pattern to `jonas` and `jonas-worker`. |
| `DockerCompose/docker-compose.yml` | Add `homeassistant` and `mcp-homeassistant` services + agent depends_on. |
| `DockerCompose/.env` | Add `HA_TOKEN=` placeholder. |
| `CLAUDE.md` | Add `mcp-homeassistant homeassistant` to Linux & Windows compose `up` commands; new "Home Assistant" section under Local Development. |

---

## Phase 1 — Domain layer

### Task 1: DTOs and `IHomeAssistantClient` contract

**Files:**
- Create: `Domain/Contracts/IHomeAssistantClient.cs`

- [ ] **Step 1: Write the contract and DTOs**

```csharp
// Domain/Contracts/IHomeAssistantClient.cs
using System.Text.Json.Nodes;
using JetBrains.Annotations;

namespace Domain.Contracts;

public interface IHomeAssistantClient
{
    Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default);
    Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default);
    Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default);
    Task<HaServiceCallResult> CallServiceAsync(
        string domain,
        string service,
        string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data,
        CancellationToken ct = default);
}

[PublicAPI]
public record HaEntityState
{
    public required string EntityId { get; init; }
    public required string State { get; init; }
    public IReadOnlyDictionary<string, JsonNode?> Attributes { get; init; } =
        new Dictionary<string, JsonNode?>();
    public DateTimeOffset? LastChanged { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
}

[PublicAPI]
public record HaServiceDefinition
{
    public required string Domain { get; init; }
    public required string Service { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, HaServiceField> Fields { get; init; } =
        new Dictionary<string, HaServiceField>();
}

[PublicAPI]
public record HaServiceField
{
    public string? Description { get; init; }
    public bool Required { get; init; }
    public JsonNode? Example { get; init; }
}

[PublicAPI]
public record HaServiceCallResult
{
    public required IReadOnlyList<HaEntityState> ChangedEntities { get; init; }
}
```

- [ ] **Step 2: Verify Domain compiles**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded, 0 errors. (No tests yet — this is a contract-only step.)

- [ ] **Step 3: Commit**

```bash
git add Domain/Contracts/IHomeAssistantClient.cs
git commit -m "feat(domain): add IHomeAssistantClient contract and HA DTOs"
```

---

### Task 2: HomeAssistant exception types

**Files:**
- Create: `Domain/Exceptions/HomeAssistantException.cs`

- [ ] **Step 1: Write exceptions**

```csharp
// Domain/Exceptions/HomeAssistantException.cs
namespace Domain.Exceptions;

public class HomeAssistantException : Exception
{
    public int? StatusCode { get; }

    public HomeAssistantException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

public sealed class HomeAssistantUnauthorizedException(string message)
    : HomeAssistantException(message, 401);

public sealed class HomeAssistantNotFoundException(string message)
    : HomeAssistantException(message, 404);
```

- [ ] **Step 2: Verify Domain compiles**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Domain/Exceptions/HomeAssistantException.cs
git commit -m "feat(domain): add HomeAssistant exception types"
```

---

### Task 3: HomeAssistantPrompt

**Files:**
- Create: `Domain/Prompts/HomeAssistantPrompt.cs`

- [ ] **Step 1: Write prompt**

```csharp
// Domain/Prompts/HomeAssistantPrompt.cs
namespace Domain.Prompts;

public static class HomeAssistantPrompt
{
    public const string Name = "home_assistant_guide";

    public const string Description =
        "Guide for controlling Home Assistant devices via the home_* generic tools";

    public const string SystemPrompt =
        """
        ## Home Assistant Control

        You can read and control any device wired into the user's Home Assistant
        instance: vacuums, lights, climate, locks, media players, sensors, switches.

        ### Discovery before action

        - **Don't guess entity IDs.** Call `home_list_entities(domain=...)` first.
        - **Don't guess service names.** Call `home_list_services(domain=...)` to see
          what's available for that domain (e.g. `vacuum.start`, `vacuum.return_to_base`,
          `light.turn_on`, `climate.set_temperature`).

        ### Calling services

        - Pass the target as the `entity_id` parameter, not nested in `data`.
          Wrong: `data={"entity_id":"vacuum.s8"}`. Right: `entity_id="vacuum.s8"`.
        - Put service-specific options in `data`, e.g. for `light.turn_on`:
          `data={"brightness_pct": 60, "color_name": "warm_white"}`.
        - For `climate.set_temperature`: `data={"temperature": 21}`.

        ### Confirming results

        - `home_call_service` returns the entities HA touched. If the list is empty
          or the state didn't change, follow up with `home_get_state(entity_id=...)`
          to read the current state and decide whether to retry.

        ### Common patterns

        - Clean a floor: `home_call_service("vacuum","start", entity_id="vacuum.<name>")`.
        - Send vacuum home: `home_call_service("vacuum","return_to_base", entity_id="vacuum.<name>")`.
        - Toggle a light: `home_call_service("light","toggle", entity_id="light.<name>")`.
        - Lock a door: `home_call_service("lock","lock", entity_id="lock.<name>")`.
        """;
}
```

- [ ] **Step 2: Verify Domain compiles**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Domain/Prompts/HomeAssistantPrompt.cs
git commit -m "feat(domain): add HomeAssistant guide prompt"
```

---

### Task 4: `HomeListEntitiesTool` (RED → GREEN → COMMIT)

**Files:**
- Create: `Tests/Unit/Domain/HomeAssistant/HomeListEntitiesToolTests.cs`
- Create: `Domain/Tools/HomeAssistant/HomeListEntitiesTool.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/HomeAssistant/HomeListEntitiesToolTests.cs
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeListEntitiesToolTests
{
    private static HaEntityState Entity(string id, string state, string? friendly = null)
        => new()
        {
            EntityId = id,
            State = state,
            Attributes = friendly is null
                ? new Dictionary<string, JsonNode?>()
                : new Dictionary<string, JsonNode?> { ["friendly_name"] = JsonValue.Create(friendly) }
        };

    [Fact]
    public async Task RunAsync_FiltersByDomain()
    {
        var client = new FakeHaClient(
            Entity("vacuum.s8", "docked", "Roborock"),
            Entity("light.kitchen", "on"),
            Entity("vacuum.spare", "cleaning"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: "vacuum", area: null, limit: 100, ct: CancellationToken.None);

        var entities = (JsonArray)result["entities"]!;
        entities.Count.ShouldBe(2);
        entities.Select(e => (string)e!["entity_id"]!).ShouldBe(["vacuum.s8", "vacuum.spare"]);
    }

    [Fact]
    public async Task RunAsync_FiltersByAreaAgainstFriendlyName()
    {
        var client = new FakeHaClient(
            Entity("light.kitchen", "on", "Kitchen Ceiling"),
            Entity("light.bedroom", "off", "Bedroom Lamp"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: null, area: "kitchen", limit: 100, ct: CancellationToken.None);

        var entities = (JsonArray)result["entities"]!;
        entities.Count.ShouldBe(1);
        ((string)entities[0]!["entity_id"]!).ShouldBe("light.kitchen");
    }

    [Fact]
    public async Task RunAsync_AppliesLimit()
    {
        var client = new FakeHaClient(
            Entity("light.a", "on"),
            Entity("light.b", "on"),
            Entity("light.c", "on"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: null, area: null, limit: 2, ct: CancellationToken.None);

        ((JsonArray)result["entities"]!).Count.ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_ProjectsExpectedFields()
    {
        var client = new FakeHaClient(Entity("light.kitchen", "on", "Kitchen"));
        var tool = new TestableHomeListEntitiesTool(client);

        var result = await tool.RunAsync(domain: null, area: null, limit: 10, ct: CancellationToken.None);

        var item = ((JsonArray)result["entities"]!)[0]!;
        ((string)item["entity_id"]!).ShouldBe("light.kitchen");
        ((string)item["state"]!).ShouldBe("on");
        ((string)item["domain"]!).ShouldBe("light");
        ((string)item["friendly_name"]!).ShouldBe("Kitchen");
    }

    private sealed class TestableHomeListEntitiesTool(IHomeAssistantClient client)
        : HomeListEntitiesTool(client)
    {
        public new Task<JsonObject> RunAsync(string? domain, string? area, int? limit, CancellationToken ct)
            => base.RunAsync(domain, area, limit, ct);
    }

    private sealed class FakeHaClient(params HaEntityState[] entities) : IHomeAssistantClient
    {
        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HaEntityState>>(entities);

        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeListEntitiesToolTests"`
Expected: Compile error or 4 failing tests with "type or namespace 'HomeListEntitiesTool' could not be found".

- [ ] **Step 3: Implement the tool**

```csharp
// Domain/Tools/HomeAssistant/HomeListEntitiesTool.cs
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant;

public class HomeListEntitiesTool(IHomeAssistantClient client)
{
    protected const string Name = "home_list_entities";

    protected const string Description =
        """
        Lists entities from Home Assistant. Filter by `domain` (e.g. 'vacuum', 'light')
        and/or `area` (substring match against friendly_name). Returns a trimmed
        projection: entity_id, state, friendly_name, domain, last_changed.
        """;

    protected async Task<JsonObject> RunAsync(string? domain, string? area, int? limit, CancellationToken ct)
    {
        var states = await client.ListStatesAsync(ct);
        var effectiveLimit = limit is > 0 ? limit.Value : 100;

        var filtered = states
            .Where(e => domain is null || EntityDomain(e.EntityId).Equals(domain, StringComparison.OrdinalIgnoreCase))
            .Where(e => area is null || FriendlyName(e).Contains(area, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .Take(effectiveLimit)
            .Select(e =>
            {
                var obj = new JsonObject
                {
                    ["entity_id"] = e.EntityId,
                    ["state"] = e.State,
                    ["domain"] = EntityDomain(e.EntityId),
                    ["friendly_name"] = FriendlyName(e)
                };
                if (e.LastChanged is { } changed)
                {
                    obj["last_changed"] = changed.ToString("O");
                }
                return obj;
            })
            .ToArray<JsonNode?>();

        return new JsonObject
        {
            ["ok"] = true,
            ["entities"] = new JsonArray(filtered)
        };
    }

    private static string EntityDomain(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot < 0 ? entityId : entityId[..dot];
    }

    private static string FriendlyName(HaEntityState entity)
        => entity.Attributes.TryGetValue("friendly_name", out var fn) && fn is JsonValue v
           && v.TryGetValue<string>(out var s) && s is not null
            ? s
            : entity.EntityId;
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeListEntitiesToolTests"`
Expected: 4 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/HomeListEntitiesTool.cs Tests/Unit/Domain/HomeAssistant/HomeListEntitiesToolTests.cs
git commit -m "feat(domain): add HomeListEntitiesTool with domain/area filtering"
```

---

### Task 5: `HomeGetStateTool` (RED → GREEN → COMMIT)

**Files:**
- Create: `Tests/Unit/Domain/HomeAssistant/HomeGetStateToolTests.cs`
- Create: `Domain/Tools/HomeAssistant/HomeGetStateTool.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/HomeAssistant/HomeGetStateToolTests.cs
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeGetStateToolTests
{
    [Fact]
    public async Task RunAsync_EntityFound_ReturnsFullState()
    {
        var entity = new HaEntityState
        {
            EntityId = "vacuum.s8",
            State = "docked",
            Attributes = new Dictionary<string, JsonNode?>
            {
                ["friendly_name"] = JsonValue.Create("Roborock"),
                ["battery_level"] = JsonValue.Create(95)
            },
            LastChanged = DateTimeOffset.Parse("2026-05-10T12:00:00Z"),
            LastUpdated = DateTimeOffset.Parse("2026-05-10T12:01:00Z")
        };
        var client = new SingleEntityClient(entity);
        var tool = new TestableHomeGetStateTool(client);

        var result = await tool.RunAsync("vacuum.s8", CancellationToken.None);

        ((bool)result["ok"]!).ShouldBeTrue();
        ((string)result["state"]!).ShouldBe("docked");
        ((int)result["attributes"]!["battery_level"]!).ShouldBe(95);
        ((string)result["last_changed"]!).ShouldBe("2026-05-10T12:00:00.0000000+00:00");
    }

    [Fact]
    public async Task RunAsync_EntityNotFound_ReturnsNotFoundEnvelope()
    {
        var client = new SingleEntityClient(null);
        var tool = new TestableHomeGetStateTool(client);

        var result = await tool.RunAsync("vacuum.missing", CancellationToken.None);

        ((bool)result["ok"]!).ShouldBeFalse();
        ((string)result["errorCode"]!).ShouldBe("not_found");
        ((string)result["message"]!).ShouldContain("vacuum.missing");
    }

    private sealed class TestableHomeGetStateTool(IHomeAssistantClient client) : HomeGetStateTool(client)
    {
        public new Task<JsonObject> RunAsync(string entityId, CancellationToken ct)
            => base.RunAsync(entityId, ct);
    }

    private sealed class SingleEntityClient(HaEntityState? entity) : IHomeAssistantClient
    {
        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => Task.FromResult(entity);

        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeGetStateToolTests"`
Expected: Compile error — `HomeGetStateTool` not found.

- [ ] **Step 3: Implement the tool**

```csharp
// Domain/Tools/HomeAssistant/HomeGetStateTool.cs
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;

namespace Domain.Tools.HomeAssistant;

public class HomeGetStateTool(IHomeAssistantClient client)
{
    protected const string Name = "home_get_state";

    protected const string Description =
        """
        Gets the current state and attributes of one Home Assistant entity by entity_id.
        Returns ok:false / errorCode:not_found if the entity does not exist.
        """;

    protected async Task<JsonObject> RunAsync(string entityId, CancellationToken ct)
    {
        var entity = await client.GetStateAsync(entityId, ct);
        if (entity is null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"Home Assistant entity '{entityId}' not found.");
        }

        var attributes = new JsonObject();
        foreach (var (key, value) in entity.Attributes)
        {
            attributes[key] = value?.DeepClone();
        }

        var result = new JsonObject
        {
            ["ok"] = true,
            ["entity_id"] = entity.EntityId,
            ["state"] = entity.State,
            ["attributes"] = attributes
        };
        if (entity.LastChanged is { } changed)
        {
            result["last_changed"] = changed.ToString("O");
        }
        if (entity.LastUpdated is { } updated)
        {
            result["last_updated"] = updated.ToString("O");
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeGetStateToolTests"`
Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/HomeGetStateTool.cs Tests/Unit/Domain/HomeAssistant/HomeGetStateToolTests.cs
git commit -m "feat(domain): add HomeGetStateTool with not-found envelope"
```

---

### Task 6: `HomeListServicesTool` (RED → GREEN → COMMIT)

**Files:**
- Create: `Tests/Unit/Domain/HomeAssistant/HomeListServicesToolTests.cs`
- Create: `Domain/Tools/HomeAssistant/HomeListServicesTool.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/HomeAssistant/HomeListServicesToolTests.cs
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeListServicesToolTests
{
    private static HaServiceDefinition Svc(string domain, string service, string? desc = null)
        => new() { Domain = domain, Service = service, Description = desc };

    [Fact]
    public async Task RunAsync_ReturnsAllServicesWhenNoFilter()
    {
        var client = new ServicesClient(
            Svc("vacuum", "start", "Start cleaning"),
            Svc("vacuum", "return_to_base"),
            Svc("light", "turn_on"));
        var tool = new TestableHomeListServicesTool(client);

        var result = await tool.RunAsync(null, CancellationToken.None);

        var arr = (JsonArray)result["services"]!;
        arr.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_FiltersByDomain()
    {
        var client = new ServicesClient(
            Svc("vacuum", "start"),
            Svc("light", "turn_on"));
        var tool = new TestableHomeListServicesTool(client);

        var result = await tool.RunAsync("vacuum", CancellationToken.None);

        var arr = (JsonArray)result["services"]!;
        arr.Count.ShouldBe(1);
        ((string)arr[0]!["domain"]!).ShouldBe("vacuum");
        ((string)arr[0]!["service"]!).ShouldBe("start");
    }

    private sealed class TestableHomeListServicesTool(IHomeAssistantClient client) : HomeListServicesTool(client)
    {
        public new Task<JsonObject> RunAsync(string? domain, CancellationToken ct) => base.RunAsync(domain, ct);
    }

    private sealed class ServicesClient(params HaServiceDefinition[] services) : IHomeAssistantClient
    {
        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HaServiceDefinition>>(services);
        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeListServicesToolTests"`
Expected: Compile error — `HomeListServicesTool` not found.

- [ ] **Step 3: Implement the tool**

```csharp
// Domain/Tools/HomeAssistant/HomeListServicesTool.cs
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant;

public class HomeListServicesTool(IHomeAssistantClient client)
{
    protected const string Name = "home_list_services";

    protected const string Description =
        """
        Lists Home Assistant services. Pass `domain` to filter (e.g. 'vacuum', 'light').
        Each entry includes domain, service, description, and field metadata so the
        agent can construct valid `data` payloads for `home_call_service`.
        """;

    protected async Task<JsonObject> RunAsync(string? domain, CancellationToken ct)
    {
        var services = await client.ListServicesAsync(ct);

        var filtered = services
            .Where(s => domain is null || s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Service, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var fields = new JsonObject();
                foreach (var (name, field) in s.Fields)
                {
                    var f = new JsonObject
                    {
                        ["required"] = field.Required
                    };
                    if (!string.IsNullOrEmpty(field.Description))
                    {
                        f["description"] = field.Description;
                    }
                    if (field.Example is not null)
                    {
                        f["example"] = field.Example.DeepClone();
                    }
                    fields[name] = f;
                }
                var item = new JsonObject
                {
                    ["domain"] = s.Domain,
                    ["service"] = s.Service,
                    ["fields"] = fields
                };
                if (!string.IsNullOrEmpty(s.Description))
                {
                    item["description"] = s.Description;
                }
                return item;
            })
            .ToArray<JsonNode?>();

        return new JsonObject
        {
            ["ok"] = true,
            ["services"] = new JsonArray(filtered)
        };
    }
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeListServicesToolTests"`
Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/HomeListServicesTool.cs Tests/Unit/Domain/HomeAssistant/HomeListServicesToolTests.cs
git commit -m "feat(domain): add HomeListServicesTool"
```

---

### Task 7: `HomeCallServiceTool` (RED → GREEN → COMMIT)

**Files:**
- Create: `Tests/Unit/Domain/HomeAssistant/HomeCallServiceToolTests.cs`
- Create: `Domain/Tools/HomeAssistant/HomeCallServiceTool.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/HomeAssistant/HomeCallServiceToolTests.cs
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeCallServiceToolTests
{
    [Fact]
    public async Task RunAsync_PassesEntityIdAndDataToClient()
    {
        var client = new RecordingClient(new HaServiceCallResult
        {
            ChangedEntities = new List<HaEntityState>
            {
                new() { EntityId = "vacuum.s8", State = "cleaning" }
            }
        });
        var tool = new TestableHomeCallServiceTool(client);

        var data = new JsonObject { ["mode"] = "spot" };
        var result = await tool.RunAsync("vacuum", "start", "vacuum.s8", data, CancellationToken.None);

        client.LastDomain.ShouldBe("vacuum");
        client.LastService.ShouldBe("start");
        client.LastEntityId.ShouldBe("vacuum.s8");
        client.LastData!["mode"]!.GetValue<string>().ShouldBe("spot");

        ((bool)result["ok"]!).ShouldBeTrue();
        var changed = (JsonArray)result["changed_entities"]!;
        ((string)changed[0]!["entity_id"]!).ShouldBe("vacuum.s8");
        ((string)changed[0]!["state"]!).ShouldBe("cleaning");
    }

    [Fact]
    public async Task RunAsync_ExplicitEntityIdParameterWinsOverDataEntityId()
    {
        var client = new RecordingClient(new HaServiceCallResult { ChangedEntities = [] });
        var tool = new TestableHomeCallServiceTool(client);

        var data = new JsonObject { ["entity_id"] = "vacuum.wrong" };
        await tool.RunAsync("vacuum", "start", "vacuum.right", data, CancellationToken.None);

        client.LastEntityId.ShouldBe("vacuum.right");
        client.LastData!.ContainsKey("entity_id").ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_AllowsNullEntityIdAndNullData()
    {
        var client = new RecordingClient(new HaServiceCallResult { ChangedEntities = [] });
        var tool = new TestableHomeCallServiceTool(client);

        var result = await tool.RunAsync("homeassistant", "restart", null, null, CancellationToken.None);

        client.LastEntityId.ShouldBeNull();
        client.LastData.ShouldBeNull();
        ((bool)result["ok"]!).ShouldBeTrue();
    }

    private sealed class TestableHomeCallServiceTool(IHomeAssistantClient client) : HomeCallServiceTool(client)
    {
        public new Task<JsonObject> RunAsync(
            string domain, string service, string? entityId, JsonObject? data, CancellationToken ct)
            => base.RunAsync(domain, service, entityId, data, ct);
    }

    private sealed class RecordingClient(HaServiceCallResult result) : IHomeAssistantClient
    {
        public string? LastDomain { get; private set; }
        public string? LastService { get; private set; }
        public string? LastEntityId { get; private set; }
        public IReadOnlyDictionary<string, JsonNode?>? LastData { get; private set; }

        public Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<HaServiceCallResult> CallServiceAsync(
            string domain, string service, string? entityId,
            IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
        {
            LastDomain = domain;
            LastService = service;
            LastEntityId = entityId;
            LastData = data;
            return Task.FromResult(result);
        }
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeCallServiceToolTests"`
Expected: Compile error — `HomeCallServiceTool` not found.

- [ ] **Step 3: Implement the tool**

```csharp
// Domain/Tools/HomeAssistant/HomeCallServiceTool.cs
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant;

public class HomeCallServiceTool(IHomeAssistantClient client)
{
    protected const string Name = "home_call_service";

    protected const string Description =
        """
        Calls a Home Assistant service. Pass `domain` and `service` (e.g. 'vacuum'/'start').
        Use the `entity_id` parameter for the target entity; service-specific options go
        in `data` as a JSON object. Returns the entities Home Assistant changed.
        """;

    protected async Task<JsonObject> RunAsync(
        string domain, string service, string? entityId, JsonObject? data, CancellationToken ct)
    {
        IReadOnlyDictionary<string, JsonNode?>? payload = null;
        if (data is not null)
        {
            var clone = new Dictionary<string, JsonNode?>();
            foreach (var (key, value) in data)
            {
                if (entityId is not null && key.Equals("entity_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                clone[key] = value?.DeepClone();
            }
            payload = clone.Count > 0 ? clone : null;
        }

        var result = await client.CallServiceAsync(domain, service, entityId, payload, ct);

        var changed = result.ChangedEntities
            .Select(e => (JsonNode?)new JsonObject
            {
                ["entity_id"] = e.EntityId,
                ["state"] = e.State
            })
            .ToArray();

        return new JsonObject
        {
            ["ok"] = true,
            ["changed_entities"] = new JsonArray(changed)
        };
    }
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeCallServiceToolTests"`
Expected: 3 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/HomeCallServiceTool.cs Tests/Unit/Domain/HomeAssistant/HomeCallServiceToolTests.cs
git commit -m "feat(domain): add HomeCallServiceTool with entity_id hoisting"
```

---

## Phase 2 — Infrastructure HTTP client

### Task 8: `HomeAssistantClient.ListStatesAsync` (RED → GREEN → COMMIT)

**Files:**
- Create: `Tests/Unit/Infrastructure/HomeAssistantClientTests.cs`
- Create: `Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Infrastructure/HomeAssistantClientTests.cs
using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

public class HomeAssistantClientTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HomeAssistantClient _client;

    public HomeAssistantClientTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new HomeAssistantClient(http, "test-token");
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ListStatesAsync_SendsBearerAndReturnsEntities()
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { entity_id = "vacuum.s8", state = "docked",
                  attributes = new Dictionary<string, object> { ["friendly_name"] = "Roborock" },
                  last_changed = "2026-05-10T12:00:00.000000+00:00",
                  last_updated = "2026-05-10T12:01:00.000000+00:00" }
        });
        _server.Given(Request.Create()
                .WithPath("/api/states")
                .WithHeader("Authorization", "Bearer test-token")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body)
                .WithHeader("Content-Type", "application/json"));

        var result = await _client.ListStatesAsync();

        result.Count.ShouldBe(1);
        result[0].EntityId.ShouldBe("vacuum.s8");
        result[0].State.ShouldBe("docked");
        result[0].Attributes["friendly_name"]!.GetValue<string>().ShouldBe("Roborock");
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests"`
Expected: Compile error — `HomeAssistantClient` not found.

- [ ] **Step 3: Implement the client (initial scaffolding + ListStatesAsync)**

```csharp
// Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.Exceptions;
using JetBrains.Annotations;

namespace Infrastructure.Clients.HomeAssistant;

public class HomeAssistantClient(HttpClient httpClient, string token) : IHomeAssistantClient
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Get, "api/states");
        using var response = await httpClient.SendAsync(request, ct);
        await EnsureOkAsync(response, ct);

        var raw = await response.Content.ReadFromJsonAsync<HaStateDto[]>(_json, ct)
                  ?? throw new HomeAssistantException("Empty Home Assistant response.");
        return raw.Select(ToEntity).ToList();
    }

    public Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<HaServiceCallResult> CallServiceAsync(
        string domain, string service, string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
        => throw new NotImplementedException();

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var snippet = await SafeReadAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new HomeAssistantUnauthorizedException(
                "Home Assistant rejected the access token (401)."),
            HttpStatusCode.NotFound => new HomeAssistantNotFoundException(
                $"Home Assistant returned 404: {snippet}"),
            _ => new HomeAssistantException(
                $"Home Assistant returned {(int)response.StatusCode}: {snippet}",
                (int)response.StatusCode)
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 200 ? body[..200] + "…" : body;
        }
        catch
        {
            return "<unreadable body>";
        }
    }

    private static HaEntityState ToEntity(HaStateDto dto) => new()
    {
        EntityId = dto.EntityId ?? string.Empty,
        State = dto.State ?? string.Empty,
        Attributes = dto.Attributes ?? new Dictionary<string, JsonNode?>(),
        LastChanged = dto.LastChanged,
        LastUpdated = dto.LastUpdated
    };

    [PublicAPI]
    private record HaStateDto
    {
        [JsonPropertyName("entity_id")] public string? EntityId { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
        [JsonPropertyName("attributes")] public Dictionary<string, JsonNode?>? Attributes { get; init; }
        [JsonPropertyName("last_changed")] public DateTimeOffset? LastChanged { get; init; }
        [JsonPropertyName("last_updated")] public DateTimeOffset? LastUpdated { get; init; }
    }
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests"`
Expected: 1 test passed.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs Tests/Unit/Infrastructure/HomeAssistantClientTests.cs
git commit -m "feat(infra): HomeAssistantClient.ListStatesAsync"
```

---

### Task 9: `GetStateAsync` (RED → GREEN → COMMIT)

**Files:**
- Modify: `Tests/Unit/Infrastructure/HomeAssistantClientTests.cs` (add tests)
- Modify: `Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs` (replace `GetStateAsync` body)

- [ ] **Step 1: Add failing tests**

Append to `HomeAssistantClientTests.cs` (inside the class):

```csharp
[Fact]
public async Task GetStateAsync_EntityFound_ReturnsState()
{
    var body = JsonSerializer.Serialize(new
    {
        entity_id = "light.kitchen",
        state = "on",
        attributes = new Dictionary<string, object> { ["brightness"] = 200 }
    });
    _server.Given(Request.Create().WithPath("/api/states/light.kitchen").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));

    var result = await _client.GetStateAsync("light.kitchen");

    result.ShouldNotBeNull();
    result!.EntityId.ShouldBe("light.kitchen");
    result.State.ShouldBe("on");
}

[Fact]
public async Task GetStateAsync_EntityMissing_ReturnsNull()
{
    _server.Given(Request.Create().WithPath("/api/states/light.missing").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(404).WithBody("Entity not found"));

    var result = await _client.GetStateAsync("light.missing");

    result.ShouldBeNull();
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests.GetStateAsync"`
Expected: Both tests fail with `NotImplementedException`.

- [ ] **Step 3: Replace `GetStateAsync` body**

In `HomeAssistantClient.cs`, replace the `GetStateAsync` placeholder with:

```csharp
public async Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default)
{
    using var request = NewRequest(HttpMethod.Get, $"api/states/{entityId}");
    using var response = await httpClient.SendAsync(request, ct);

    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        return null;
    }
    await EnsureOkAsync(response, ct);

    var dto = await response.Content.ReadFromJsonAsync<HaStateDto>(_json, ct);
    return dto is null ? null : ToEntity(dto);
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests.GetStateAsync"`
Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs Tests/Unit/Infrastructure/HomeAssistantClientTests.cs
git commit -m "feat(infra): HomeAssistantClient.GetStateAsync with 404→null"
```

---

### Task 10: `ListServicesAsync` (RED → GREEN → COMMIT)

**Files:**
- Modify: `Tests/Unit/Infrastructure/HomeAssistantClientTests.cs`
- Modify: `Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs`

- [ ] **Step 1: Add failing test**

Append to `HomeAssistantClientTests.cs`:

```csharp
[Fact]
public async Task ListServicesAsync_FlattensNestedDomainShape()
{
    var body = JsonSerializer.Serialize(new[]
    {
        new
        {
            domain = "vacuum",
            services = new Dictionary<string, object>
            {
                ["start"] = new
                {
                    description = "Start cleaning",
                    fields = new Dictionary<string, object>
                    {
                        ["entity_id"] = new
                        {
                            description = "Target",
                            required = true,
                            example = "vacuum.s8"
                        }
                    }
                },
                ["return_to_base"] = new { description = "Send home", fields = new Dictionary<string, object>() }
            }
        }
    });
    _server.Given(Request.Create().WithPath("/api/services").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));

    var result = await _client.ListServicesAsync();

    result.Count.ShouldBe(2);
    var start = result.Single(s => s.Service == "start");
    start.Domain.ShouldBe("vacuum");
    start.Description.ShouldBe("Start cleaning");
    start.Fields["entity_id"].Required.ShouldBeTrue();
    start.Fields["entity_id"].Description.ShouldBe("Target");
    start.Fields["entity_id"].Example!.GetValue<string>().ShouldBe("vacuum.s8");
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests.ListServicesAsync"`
Expected: Fails with `NotImplementedException`.

- [ ] **Step 3: Replace `ListServicesAsync` body and add helper DTOs**

In `HomeAssistantClient.cs`, replace the `ListServicesAsync` placeholder:

```csharp
public async Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default)
{
    using var request = NewRequest(HttpMethod.Get, "api/services");
    using var response = await httpClient.SendAsync(request, ct);
    await EnsureOkAsync(response, ct);

    var domains = await response.Content.ReadFromJsonAsync<HaServiceDomainDto[]>(_json, ct)
                  ?? throw new HomeAssistantException("Empty services payload.");

    return domains
        .SelectMany(d => (d.Services ?? new Dictionary<string, HaServiceDto>())
            .Select(kv => new HaServiceDefinition
            {
                Domain = d.Domain ?? string.Empty,
                Service = kv.Key,
                Description = kv.Value.Description,
                Fields = (kv.Value.Fields ?? new Dictionary<string, HaServiceFieldDto>())
                    .ToDictionary(f => f.Key, f => new HaServiceField
                    {
                        Description = f.Value.Description,
                        Required = f.Value.Required ?? false,
                        Example = f.Value.Example
                    })
            }))
        .ToList();
}
```

Append the supporting DTOs inside the class (next to `HaStateDto`):

```csharp
[PublicAPI]
private record HaServiceDomainDto
{
    [JsonPropertyName("domain")] public string? Domain { get; init; }
    [JsonPropertyName("services")] public Dictionary<string, HaServiceDto>? Services { get; init; }
}

[PublicAPI]
private record HaServiceDto
{
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("fields")] public Dictionary<string, HaServiceFieldDto>? Fields { get; init; }
}

[PublicAPI]
private record HaServiceFieldDto
{
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("required")] public bool? Required { get; init; }
    [JsonPropertyName("example")] public JsonNode? Example { get; init; }
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests.ListServicesAsync"`
Expected: 1 test passed.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs Tests/Unit/Infrastructure/HomeAssistantClientTests.cs
git commit -m "feat(infra): HomeAssistantClient.ListServicesAsync flattens HA's domain shape"
```

---

### Task 11: `CallServiceAsync` (RED → GREEN → COMMIT)

**Files:**
- Modify: `Tests/Unit/Infrastructure/HomeAssistantClientTests.cs`
- Modify: `Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs`

- [ ] **Step 1: Add failing tests**

Append:

```csharp
[Fact]
public async Task CallServiceAsync_HoistsEntityIdIntoTargetAndSendsBody()
{
    var responseBody = JsonSerializer.Serialize(new[]
    {
        new { entity_id = "vacuum.s8", state = "cleaning", attributes = new Dictionary<string, object>() }
    });
    _server.Given(Request.Create()
            .WithPath("/api/services/vacuum/start")
            .WithHeader("Authorization", "Bearer test-token")
            .UsingPost())
        .RespondWith(Response.Create().WithStatusCode(200).WithBody(responseBody));

    var data = new Dictionary<string, JsonNode?> { ["mode"] = JsonValue.Create("spot") };
    var result = await _client.CallServiceAsync("vacuum", "start", "vacuum.s8", data);

    result.ChangedEntities.Count.ShouldBe(1);
    result.ChangedEntities[0].EntityId.ShouldBe("vacuum.s8");
    result.ChangedEntities[0].State.ShouldBe("cleaning");

    var calls = _server.LogEntries.ToList();
    var posted = JsonNode.Parse(calls.Last().RequestMessage.Body!)!.AsObject();
    posted["target"]!["entity_id"]!.GetValue<string>().ShouldBe("vacuum.s8");
    posted["mode"]!.GetValue<string>().ShouldBe("spot");
}

[Fact]
public async Task CallServiceAsync_NoEntityId_OmitsTarget()
{
    _server.Given(Request.Create().WithPath("/api/services/homeassistant/restart").UsingPost())
        .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

    await _client.CallServiceAsync("homeassistant", "restart", null, null);

    var posted = JsonNode.Parse(_server.LogEntries.Last().RequestMessage.Body!)!.AsObject();
    posted.ContainsKey("target").ShouldBeFalse();
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests.CallServiceAsync"`
Expected: Both fail with `NotImplementedException`.

- [ ] **Step 3: Implement `CallServiceAsync`**

Replace the `CallServiceAsync` placeholder:

```csharp
public async Task<HaServiceCallResult> CallServiceAsync(
    string domain, string service, string? entityId,
    IReadOnlyDictionary<string, JsonNode?>? data, CancellationToken ct = default)
{
    var body = new JsonObject();
    if (data is not null)
    {
        foreach (var (key, value) in data)
        {
            body[key] = value?.DeepClone();
        }
    }
    if (!string.IsNullOrEmpty(entityId))
    {
        body["target"] = new JsonObject { ["entity_id"] = entityId };
    }

    using var request = NewRequest(HttpMethod.Post, $"api/services/{domain}/{service}");
    request.Content = JsonContent.Create(body);

    using var response = await httpClient.SendAsync(request, ct);
    await EnsureOkAsync(response, ct);

    var raw = await response.Content.ReadFromJsonAsync<HaStateDto[]>(_json, ct) ?? [];
    return new HaServiceCallResult { ChangedEntities = raw.Select(ToEntity).ToList() };
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests.CallServiceAsync"`
Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/HomeAssistant/HomeAssistantClient.cs Tests/Unit/Infrastructure/HomeAssistantClientTests.cs
git commit -m "feat(infra): HomeAssistantClient.CallServiceAsync hoists entity_id into target"
```

---

### Task 12: 401 → `HomeAssistantUnauthorizedException` (RED → GREEN → COMMIT)

**Files:**
- Modify: `Tests/Unit/Infrastructure/HomeAssistantClientTests.cs`

- [ ] **Step 1: Add failing test**

Append:

```csharp
[Fact]
public async Task ListStatesAsync_401_ThrowsUnauthorized()
{
    _server.Given(Request.Create().WithPath("/api/states").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(401).WithBody("Unauthorized"));

    var ex = await Should.ThrowAsync<HomeAssistantUnauthorizedException>(
        () => _client.ListStatesAsync());
    ex.StatusCode.ShouldBe(401);
}
```

- [ ] **Step 2: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantClientTests.ListStatesAsync_401"`
Expected: 1 test passed (mapping is already in `EnsureOkAsync` from Task 8 — this test confirms it).

If it fails for any reason (e.g. retry policy intercepting), fix `EnsureOkAsync` so 401 short-circuits before any retry decision; do not retry on 401.

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Infrastructure/HomeAssistantClientTests.cs
git commit -m "test(infra): cover HomeAssistant 401 unauthorized mapping"
```

---

### Task 13: DI registration extension

**Files:**
- Create: `Infrastructure/Extensions/HomeAssistantClientExtensions.cs`

- [ ] **Step 1: Write the extension**

```csharp
// Infrastructure/Extensions/HomeAssistantClientExtensions.cs
using Domain.Contracts;
using Infrastructure.Clients.HomeAssistant;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

public static class HomeAssistantClientExtensions
{
    public static IServiceCollection AddHomeAssistantClient(
        this IServiceCollection services, string baseUrl, string token)
    {
        services.AddHttpClient<IHomeAssistantClient, HomeAssistantClient>((http, _) =>
        {
            http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            http.Timeout = TimeSpan.FromSeconds(30);
            return new HomeAssistantClient(http, token);
        })
        .AddRetryWithExponentialWaitPolicy(
            attempts: 2,
            waitTime: TimeSpan.FromSeconds(1),
            attemptTimeout: TimeSpan.FromSeconds(15));

        return services;
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Infrastructure/Extensions/HomeAssistantClientExtensions.cs
git commit -m "feat(infra): AddHomeAssistantClient DI extension with retry policy"
```

---

## Phase 3 — `McpServerHomeAssistant` project

### Task 14: Project skeleton (csproj, Program, Settings, Dockerfile, appsettings)

**Files:**
- Create: `McpServerHomeAssistant/McpServerHomeAssistant.csproj`
- Create: `McpServerHomeAssistant/Program.cs`
- Create: `McpServerHomeAssistant/Settings/McpSettings.cs`
- Create: `McpServerHomeAssistant/Dockerfile`
- Create: `McpServerHomeAssistant/appsettings.json`

- [ ] **Step 1: Create `McpServerHomeAssistant.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <LangVersion>14</LangVersion>
    <UserSecretsId>1ad2c5b9-7e4f-4a3b-9c8d-2f1e6a4b8c30</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="..\.dockerignore">
      <Link>.dockerignore</Link>
    </Content>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.7" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.2.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create `Program.cs`**

```csharp
// McpServerHomeAssistant/Program.cs
using McpServerHomeAssistant.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();
```

- [ ] **Step 3: Create `Settings/McpSettings.cs`**

```csharp
// McpServerHomeAssistant/Settings/McpSettings.cs
namespace McpServerHomeAssistant.Settings;

public record McpSettings
{
    public required HomeAssistantConfiguration HomeAssistant { get; init; }
}

public record HomeAssistantConfiguration
{
    public required string BaseUrl { get; init; }
    public required string Token { get; init; }
}
```

- [ ] **Step 4: Create `Dockerfile`**

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerHomeAssistant/McpServerHomeAssistant.csproj", "McpServerHomeAssistant/"]
RUN dotnet restore "McpServerHomeAssistant/McpServerHomeAssistant.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerHomeAssistant/", "McpServerHomeAssistant/"]
WORKDIR "/src/McpServerHomeAssistant"
RUN dotnet publish "./McpServerHomeAssistant.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerHomeAssistant.dll"]
```

- [ ] **Step 5: Create `appsettings.json`**

```json
{
  "HomeAssistant": {
    "BaseUrl": "http://homeassistant:8123",
    "Token": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

- [ ] **Step 6: Do NOT add to `agent.sln` and do NOT commit yet**

The project skeleton references `Modules.ConfigModule` (in `Program.cs`) which doesn't exist yet. Adding it to the solution now would break `dotnet build agent.sln`. The sln addition and the first build/commit happen at the end of Task 15 once the module and tool wrappers exist.

Leave the new files staged on disk (do not `git add`) until Task 15 completes.

---

### Task 15: `ConfigModule` and tool/prompt wrappers

**Files:**
- Create: `McpServerHomeAssistant/Modules/ConfigModule.cs`
- Create: `McpServerHomeAssistant/McpTools/McpHomeListEntitiesTool.cs`
- Create: `McpServerHomeAssistant/McpTools/McpHomeGetStateTool.cs`
- Create: `McpServerHomeAssistant/McpTools/McpHomeListServicesTool.cs`
- Create: `McpServerHomeAssistant/McpTools/McpHomeCallServiceTool.cs`
- Create: `McpServerHomeAssistant/McpPrompts/McpSystemPrompt.cs`

- [ ] **Step 1: Create `ConfigModule.cs`**

```csharp
// McpServerHomeAssistant/Modules/ConfigModule.cs
using Infrastructure.Extensions;
using Infrastructure.Utils;
using McpServerHomeAssistant.McpPrompts;
using McpServerHomeAssistant.McpTools;
using McpServerHomeAssistant.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerHomeAssistant.Modules;

public static class ConfigModule
{
    public static McpSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<McpSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    extension(IServiceCollection services)
    {
        public IServiceCollection ConfigureMcp(McpSettings settings)
        {
            services
                .AddSingleton(settings)
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
                .AddMcpServer()
                .WithHttpTransport()
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    try
                    {
                        return await next(context, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                        logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                        return ToolResponse.Create(ex);
                    }
                }))
                .WithTools<McpHomeListEntitiesTool>()
                .WithTools<McpHomeGetStateTool>()
                .WithTools<McpHomeListServicesTool>()
                .WithTools<McpHomeCallServiceTool>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }
    }
}
```

- [ ] **Step 2: Create `McpTools/McpHomeListEntitiesTool.cs`**

```csharp
// McpServerHomeAssistant/McpTools/McpHomeListEntitiesTool.cs
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeListEntitiesTool(IHomeAssistantClient client) : HomeListEntitiesTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Optional domain filter, e.g. 'vacuum', 'light', 'climate'")] string? domain = null,
        [Description("Optional substring to match against friendly_name")] string? area = null,
        [Description("Maximum number of entities to return (default 100)")] int? limit = 100,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, area, limit, ct));
    }
}
```

- [ ] **Step 3: Create `McpTools/McpHomeGetStateTool.cs`**

```csharp
// McpServerHomeAssistant/McpTools/McpHomeGetStateTool.cs
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeGetStateTool(IHomeAssistantClient client) : HomeGetStateTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Entity ID, e.g. 'vacuum.roborock_s8'")] string entityId,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(entityId, ct));
    }
}
```

- [ ] **Step 4: Create `McpTools/McpHomeListServicesTool.cs`**

```csharp
// McpServerHomeAssistant/McpTools/McpHomeListServicesTool.cs
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeListServicesTool(IHomeAssistantClient client) : HomeListServicesTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Optional domain filter, e.g. 'vacuum', 'light'")] string? domain = null,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, ct));
    }
}
```

- [ ] **Step 5: Create `McpTools/McpHomeCallServiceTool.cs`**

```csharp
// McpServerHomeAssistant/McpTools/McpHomeCallServiceTool.cs
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpTools;

[McpServerToolType]
public class McpHomeCallServiceTool(IHomeAssistantClient client) : HomeCallServiceTool(client)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("Service domain, e.g. 'vacuum', 'light'")] string domain,
        [Description("Service name, e.g. 'start', 'turn_on'")] string service,
        [Description("Optional target entity_id, e.g. 'vacuum.roborock_s8'")] string? entityId = null,
        [Description("Optional service-specific data as a JSON object, e.g. {\"brightness_pct\": 60}")]
        JsonObject? data = null,
        CancellationToken ct = default)
    {
        return ToolResponse.Create(await RunAsync(domain, service, entityId, data, ct));
    }
}
```

- [ ] **Step 6: Create `McpPrompts/McpSystemPrompt.cs`**

```csharp
// McpServerHomeAssistant/McpPrompts/McpSystemPrompt.cs
using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = HomeAssistantPrompt.Name)]
    [Description(HomeAssistantPrompt.Description)]
    public static string GetSystemPrompt() => HomeAssistantPrompt.SystemPrompt;
}
```

- [ ] **Step 7: Add the project to `agent.sln`**

Run from the repo root:

```bash
dotnet sln agent.sln add McpServerHomeAssistant/McpServerHomeAssistant.csproj
```

Expected: "Project `McpServerHomeAssistant\McpServerHomeAssistant.csproj` added to the solution."

- [ ] **Step 8: Build the new project**

Run: `dotnet build McpServerHomeAssistant/McpServerHomeAssistant.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 9: Commit the entire McpServerHomeAssistant skeleton + module + tools + prompt**

```bash
git add agent.sln McpServerHomeAssistant/
git commit -m "feat: McpServerHomeAssistant project with 4 generic HA tools and prompt"
```

---

## Phase 4 — Docker Compose, env, and agent wiring

### Task 16: Add `homeassistant` and `mcp-homeassistant` services

**Files:**
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Add the `homeassistant` service**

Insert this block immediately after the `qbittorrent:` service (around line 53, before `filebrowser:`). Use the exact same indentation (2 spaces) as the surrounding services:

```yaml
  homeassistant:
    image: ghcr.io/home-assistant/home-assistant:stable
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: homeassistant
    ports:
      - "8123:8123"
    volumes:
      - ./volumes/homeassistant_config:/config
    environment:
      - TZ=Europe/Madrid
    restart: unless-stopped
    networks:
      - jackbot
```

- [ ] **Step 2: Add the `mcp-homeassistant` service**

Insert immediately after the `mcp-idealista:` block (around line 308, before `mcp-channel-signalr:`):

```yaml
  mcp-homeassistant:
    image: mcp-homeassistant:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: mcp-homeassistant
    ports:
      - "6006:8080"
    build:
      context: ${REPOSITORY_PATH}
      dockerfile: McpServerHomeAssistant/Dockerfile
      cache_from:
        - mcp-homeassistant:latest
      args:
        - BUILDKIT_INLINE_CACHE=1
    restart: unless-stopped
    env_file:
      - .env
    networks:
      - jackbot
    depends_on:
      base-sdk:
        condition: service_started
      homeassistant:
        condition: service_started
```

- [ ] **Step 3: Add `mcp-homeassistant` to the agent service's `depends_on`**

In the `agent:` block (around line 419 in the existing file, where `mcp-idealista:` already appears under `depends_on`), insert the new entry immediately after the `mcp-idealista` line:

```yaml
      mcp-homeassistant:
        condition: service_started
```

- [ ] **Step 4: Validate the compose file parses**

Run from the repo root:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot config > /dev/null
```

Expected: no output (empty stdout) and exit code 0.

- [ ] **Step 5: Commit**

```bash
git add DockerCompose/docker-compose.yml
git commit -m "ops: add homeassistant and mcp-homeassistant compose services"
```

---

### Task 17: `.env` placeholder and `appsettings.json` defaults

**Files:**
- Modify: `DockerCompose/.env`

- [ ] **Step 1: Add `HA_TOKEN` placeholder**

Append the following block at the end of `DockerCompose/.env` (just before `# Cloudflare secrets` if present, otherwise at the end):

```
# Home Assistant secrets
HOMEASSISTANT__TOKEN=
```

Note: the env-var key matches `appsettings.json` two-underscore convention so .NET configuration binds it to `HomeAssistant:Token`. (`mcp-homeassistant`'s docker-compose `env_file: .env` makes this available inside the container.)

- [ ] **Step 2: Verify the value is wired through**

Run: `grep "^HOMEASSISTANT__TOKEN" DockerCompose/.env`
Expected: one matching line with empty value.

- [ ] **Step 3: Commit**

```bash
git add DockerCompose/.env
git commit -m "ops: add HOMEASSISTANT__TOKEN placeholder to compose env"
```

---

### Task 18: Wire MCP endpoint and whitelist into agent config

**Files:**
- Modify: `Agent/appsettings.json`

- [ ] **Step 1: Add the MCP endpoint to `jonas` agent and `jonas-worker` subagent**

In `Agent/appsettings.json`:

In the `jonas` agent's `mcpServerEndpoints` array (the one already containing `http://mcp-idealista:8080/mcp`), append:

```json
"http://mcp-homeassistant:8080/mcp"
```

In the `jonas` agent's `whitelistPatterns` array, append:

```json
"mcp__mcp-homeassistant*"
```

Do the same for the `jonas-worker` entry under `subAgents` (only `mcpServerEndpoints` — `subAgents` has no `whitelistPatterns` field).

- [ ] **Step 2: Verify the file is still valid JSON**

Run: `python3 -c "import json; json.load(open('Agent/appsettings.json'))"`
Expected: no output (success).

- [ ] **Step 3: Commit**

```bash
git add Agent/appsettings.json
git commit -m "feat(agent): wire mcp-homeassistant endpoint and whitelist for jonas"
```

---

### Task 19: Update `CLAUDE.md` compose commands and add HA section

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add `mcp-homeassistant homeassistant` to both `up` commands**

In the **Linux / WSL** command block, replace:

```
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus qbittorrent jackett redis caddy camoufox
```

with:

```
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus qbittorrent jackett redis caddy camoufox homeassistant
```

Apply the same change to the **Windows** command block (replace `override.linux.yml` with `override.windows.yml`).

- [ ] **Step 2: Add the "Accessing Home Assistant" section**

Append this block under "Local Development" (after the existing `### Accessing the Dashboard` subsection):

```markdown
### Accessing Home Assistant

Home Assistant runs at `http://<host>:8123` (port published on all interfaces so you can configure it from any LAN machine). On first run:

1. Create the owner account through the browser onboarding flow.
2. From the user profile menu, open **Security → Long-Lived Access Tokens** and create one.
3. Set `HOMEASSISTANT__TOKEN=...` in `DockerCompose/.env` and restart the `mcp-homeassistant` container.
4. To control the Roborock S8: Settings → Devices & Services → Add Integration → **Roborock**, log in with the Roborock account; the vacuum appears as `vacuum.<name>` once the integration finishes.

The agent reaches HA inside the compose network at `http://homeassistant:8123` via the `McpServerHomeAssistant` MCP server.
```

- [ ] **Step 3: Verify the file**

Run: `grep -c "mcp-homeassistant" CLAUDE.md`
Expected: `2` (one in each compose command block).

Run: `grep -c "Accessing Home Assistant" CLAUDE.md`
Expected: `1`.

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: add Home Assistant to compose commands and developer guide"
```

---

## Phase 5 — Final verification

### Task 20: Full solution build and test

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build agent.sln`
Expected: Build succeeded, 0 errors. (Warnings tolerated.)

- [ ] **Step 2: Run the full test suite (filtered to relevant tests)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistant"`
Expected: All HA-related tests pass (the 9 unit tests added across Tasks 4–7, plus the 6 client tests across Tasks 8–12 — 15 total).

- [ ] **Step 3: Manual smoke test (informational, not a git step)**

Outside the agent, ask the user to:
1. Pull / build: `docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build homeassistant mcp-homeassistant`
2. Browse to `http://<host>:8123`, complete onboarding, generate a token, set it in `.env`, restart `mcp-homeassistant`.
3. Add the Roborock integration in HA.
4. From a chat with the `jonas` agent: "list my vacuums" → should produce a `home_list_entities(domain="vacuum")` call returning the S8.
5. "send the vacuum home" → should call `home_call_service(domain="vacuum", service="return_to_base", entity_id="vacuum.<name>")`.

No commit for this step — the smoke test confirms the system works end-to-end.

---

## Spec coverage check

| Spec section | Plan task |
|---|---|
| `IHomeAssistantClient` contract + DTOs | Task 1 |
| `HaEntityState`, `HaServiceDefinition`, `HaServiceField`, `HaServiceCallResult` records | Task 1 |
| Typed exceptions | Task 2 |
| `HomeAssistantPrompt` constants | Task 3 |
| `HomeListEntitiesTool` projection + filtering | Task 4 |
| `HomeGetStateTool` 404 → `{ok:false}` | Task 5 |
| `HomeListServicesTool` flatten | Task 6 |
| `HomeCallServiceTool` `entity_id` hoist + precedence | Task 7 |
| `HomeAssistantClient` HTTP, Bearer, retry | Tasks 8–11, 13 |
| 401/404 mapping | Tasks 9, 12 |
| `AddHomeAssistantClient` DI extension | Task 13 |
| `McpServerHomeAssistant` project skeleton | Task 14 |
| `ConfigModule`, four `McpHome*Tool`, `McpSystemPrompt` registration | Task 15 |
| `homeassistant` + `mcp-homeassistant` compose services with bind mount, LAN port | Task 16 |
| `HOMEASSISTANT__TOKEN` env placeholder | Task 17 |
| Agent endpoint registration + whitelist | Task 18 |
| CLAUDE.md compose commands + HA setup section | Task 19 |
| Build/test verification | Task 20 |
