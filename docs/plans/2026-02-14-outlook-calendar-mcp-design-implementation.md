# Outlook Calendar MCP Server Implementation Plan

> **For Claude:** Execute this plan using subagents. Dispatch a fresh subagent per task
> using the Task tool (subagent_type: "general-purpose"). Each task is self-contained.
> NEVER skip test or review tasks. They are tracked separately and all must complete.

**Goal:** Add Outlook Calendar integration via a new MCP server with per-user delegated OAuth2, multi-calendar support, and a provider abstraction for future backends.

**Architecture:** Domain layer (contracts, DTOs, tools) → Infrastructure (Graph REST client, Redis token store, OAuth service) → McpServerCalendar (thin MCP wrappers on port 6006) → Agent (OAuth endpoints, token injection) → WebChat.Client (Connected Accounts UI). The MCP server is stateless — it receives an access token per-call.

**Tech Stack:** .NET 10, Microsoft Graph REST API (via HttpClient), MSAL (`Microsoft.Identity.Client`), Redis (StackExchange.Redis), Blazor WASM, xUnit, Shouldly, Moq, WireMock.Net

**Design Document:** `docs/plans/2026-02-14-outlook-calendar-mcp-design.md`

**Design Refinements (from implementation analysis):**
1. `ICalendarProvider` uses `accessToken` (not `userId`) as first parameter — aligns with revised architecture where MCP server is stateless and receives tokens per-call from the Agent.
2. `MicrosoftGraphCalendarProvider` uses `HttpClient` directly against Graph REST API (not Graph SDK) — simpler to test with WireMock, fewer dependencies, consistent with existing patterns (`OpenRouterEmbeddingService`).

---

## Task 0: Scaffolding

**Type:** SCAFFOLDING (no triplet — pure configuration)
**Depends on:** none

Create project structure, DTOs, contracts, and infrastructure config. No business logic.

**Files to create:**

1. `McpServerCalendar/McpServerCalendar.csproj` — follow McpServerMemory pattern:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <LangVersion>14</LangVersion>
    <UserSecretsId>[generate-new-guid]</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <Content Include="..\.dockerignore"><Link>.dockerignore</Link></Content>
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.3" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="0.8.0-preview.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj"/>
  </ItemGroup>
  <ItemGroup>
    <None Update="appsettings.json"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None>
  </ItemGroup>
</Project>
```

2. `McpServerCalendar/Program.cs` — minimal startup:
```csharp
using McpServerCalendar.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.MapMcp();

await app.RunAsync();
```

3. `McpServerCalendar/Modules/ConfigModule.cs` — skeleton (MCP tools registered later in Feature 4):
```csharp
using McpServerCalendar.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerCalendar.Modules;

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

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services.AddMcpServer().WithHttpTransport();
        return services;
    }
}
```

4. `McpServerCalendar/Settings/McpSettings.cs`:
```csharp
namespace McpServerCalendar.Settings;

public record McpSettings;
```

5. `McpServerCalendar/appsettings.json`:
```json
{}
```

6. `McpServerCalendar/Dockerfile` — follow McpServerMemory pattern, replace "McpServerMemory" with "McpServerCalendar" throughout.

7. `Domain/DTOs/Calendar.cs` — all calendar DTOs:
```csharp
namespace Domain.DTOs;

public record CalendarInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Color { get; init; }
    public bool IsDefault { get; init; }
    public bool CanEdit { get; init; }
}

public record CalendarEvent
{
    public required string Id { get; init; }
    public string? CalendarId { get; init; }
    public required string Subject { get; init; }
    public string? Body { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string? Location { get; init; }
    public bool IsAllDay { get; init; }
    public string? Recurrence { get; init; }
    public IReadOnlyList<string> Attendees { get; init; } = [];
    public string? Organizer { get; init; }
    public string? Status { get; init; }
}

public record EventCreateRequest
{
    public string? CalendarId { get; init; }
    public required string Subject { get; init; }
    public string? Body { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string? Location { get; init; }
    public bool? IsAllDay { get; init; }
    public IReadOnlyList<string>? Attendees { get; init; }
    public string? Recurrence { get; init; }
}

public record EventUpdateRequest
{
    public string? Subject { get; init; }
    public string? Body { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? Location { get; init; }
    public bool? IsAllDay { get; init; }
    public IReadOnlyList<string>? Attendees { get; init; }
    public string? Recurrence { get; init; }
}

public record FreeBusySlot
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required FreeBusyStatus Status { get; init; }
}

public enum FreeBusyStatus
{
    Free,
    Busy,
    Tentative,
    OutOfOffice
}

public record OAuthTokens
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
```

8. `Domain/Contracts/ICalendarProvider.cs`:
```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface ICalendarProvider
{
    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accessToken, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string accessToken, string? calendarId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
    Task<CalendarEvent> GetEventAsync(string accessToken, string eventId, string? calendarId, CancellationToken ct = default);
    Task<CalendarEvent> CreateEventAsync(string accessToken, EventCreateRequest request, CancellationToken ct = default);
    Task<CalendarEvent> UpdateEventAsync(string accessToken, string eventId, EventUpdateRequest request, CancellationToken ct = default);
    Task DeleteEventAsync(string accessToken, string eventId, string? calendarId, CancellationToken ct = default);
    Task<IReadOnlyList<FreeBusySlot>> CheckAvailabilityAsync(string accessToken, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
}
```

9. `Domain/Contracts/ICalendarTokenStore.cs`:
```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface ICalendarTokenStore
{
    Task<OAuthTokens?> GetTokensAsync(string userId, CancellationToken ct = default);
    Task StoreTokensAsync(string userId, OAuthTokens tokens, CancellationToken ct = default);
    Task RemoveTokensAsync(string userId, CancellationToken ct = default);
    Task<bool> HasTokensAsync(string userId, CancellationToken ct = default);
}
```

**Files to modify:**

10. `DockerCompose/docker-compose.yml` — add `mcp-calendar` service before the `agent` service (follow mcp-memory pattern, port 6006:8080, no Redis dependency since it's stateless).

11. `DockerCompose/.env` — add Microsoft OAuth placeholders:
```
MICROSOFT__CLIENTID=
MICROSOFT__CLIENTSECRET=
MICROSOFT__TENANTID=
```

12. `Agent/appsettings.json` — add `"http://mcp-calendar:8080/sse"` to the `mcpServerEndpoints` array and `"mcp:mcp-calendar:*"` to `whitelistPatterns` for agents that should access calendars.

13. `Tests/Tests.csproj` — add project reference:
```xml
<ProjectReference Include="..\McpServerCalendar\McpServerCalendar.csproj"/>
```

14. `Agent.sln` — add McpServerCalendar project via `dotnet sln add`.

**Verification:**
```bash
dotnet build
```
Expected: Solution builds with no errors.

**Commit:** `git commit -m "build: scaffold McpServerCalendar project with domain contracts and DTOs"`

---

## Feature 1: Domain Calendar Tools

### Task 1.1: Write failing tests for Domain Calendar Tools

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 0

**Design requirements being tested:**
- 7 domain tools, each wrapping a single `ICalendarProvider` method
- Each tool has `Name` and `Description` constants for MCP reuse
- Each tool has a `protected async Task<JsonNode> Run(...)` method
- CalendarListTool: returns JSON array of calendars (id, name, isDefault, canEdit)
- EventListTool: validates date range, returns events filtered by optional calendarId
- EventGetTool: returns full event details as JSON
- EventCreateTool: delegates to provider, returns created event
- EventUpdateTool: delegates to provider with patch semantics
- EventDeleteTool: delegates to provider, returns confirmation
- CheckAvailabilityTool: returns free/busy slots

**Files:**
- Create: `Tests/Unit/Domain/Calendar/CalendarListToolTests.cs`
- Create: `Tests/Unit/Domain/Calendar/EventListToolTests.cs`
- Create: `Tests/Unit/Domain/Calendar/EventGetToolTests.cs`
- Create: `Tests/Unit/Domain/Calendar/EventCreateToolTests.cs`
- Create: `Tests/Unit/Domain/Calendar/EventUpdateToolTests.cs`
- Create: `Tests/Unit/Domain/Calendar/EventDeleteToolTests.cs`
- Create: `Tests/Unit/Domain/Calendar/CheckAvailabilityToolTests.cs`

**What to test:**

Each tool class lives in `Domain/Tools/Calendar/` and takes `ICalendarProvider` via primary constructor. Use Moq to mock `ICalendarProvider` and Shouldly for assertions. Follow existing test patterns (see `Tests/Unit/Infrastructure/Memory/` for examples).

```csharp
// Tests/Unit/Domain/Calendar/CalendarListToolTests.cs
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class CalendarListToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly CalendarListTool _tool;

    public CalendarListToolTests()
    {
        _tool = new CalendarListTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_ReturnsCalendarsAsJsonArray()
    {
        // Arrange
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-1", Name = "Personal", IsDefault = true, CanEdit = true },
            new() { Id = "cal-2", Name = "Work", IsDefault = false, CanEdit = true }
        };
        _providerMock.Setup(p => p.ListCalendarsAsync("token-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        // Act — Run is protected, so use a test subclass or InternalsVisibleTo
        var result = await InvokeRun("token-123");

        // Assert
        var array = result.AsArray();
        array.Count.ShouldBe(2);
        array[0]!["id"]!.GetValue<string>().ShouldBe("cal-1");
        array[0]!["name"]!.GetValue<string>().ShouldBe("Personal");
        array[0]!["isDefault"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Run_WhenNoCalendars_ReturnsEmptyArray()
    {
        _providerMock.Setup(p => p.ListCalendarsAsync("token-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarInfo>());

        var result = await InvokeRun("token-123");

        result.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        // Verify Name and Description constants exist and are non-empty
        CalendarListTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        CalendarListTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }

    // Helper to invoke protected Run — create a testable subclass or use reflection
    private Task<JsonNode> InvokeRun(string accessToken, CancellationToken ct = default)
    {
        // Use a test subclass that exposes Run publicly
        var testable = new TestableCalendarListTool(_providerMock.Object);
        return testable.InvokeRun(accessToken, ct);
    }
}

// Test helper to expose protected Run method
internal class TestableCalendarListTool(ICalendarProvider provider) : CalendarListTool(provider)
{
    public Task<JsonNode> InvokeRun(string accessToken, CancellationToken ct = default)
        => Run(accessToken, ct);
}
```

```csharp
// Tests/Unit/Domain/Calendar/EventListToolTests.cs
namespace Tests.Unit.Domain.Calendar;

public class EventListToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();

    [Fact]
    public async Task Run_WithDateRange_DelegatesToProvider()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddDays(7);
        var events = new List<CalendarEvent>
        {
            new() { Id = "evt-1", Subject = "Meeting", Start = start.AddHours(1), End = start.AddHours(2) }
        };
        _providerMock.Setup(p => p.ListEventsAsync("token", null, start, end, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        var tool = new TestableEventListTool(_providerMock.Object);
        var result = await tool.InvokeRun("token", null, start, end);

        result.AsArray().Count.ShouldBe(1);
        result.AsArray()[0]!["subject"]!.GetValue<string>().ShouldBe("Meeting");
    }

    [Fact]
    public async Task Run_WithCalendarId_PassesItToProvider()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddDays(1);
        _providerMock.Setup(p => p.ListEventsAsync("token", "cal-1", start, end, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarEvent>());

        var tool = new TestableEventListTool(_providerMock.Object);
        await tool.InvokeRun("token", "cal-1", start, end);

        _providerMock.Verify(p => p.ListEventsAsync("token", "cal-1", start, end, It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

Follow the same pattern for the remaining 5 tools. Key tests for each:

- **EventGetToolTests**: verifies provider is called with correct eventId and calendarId, verifies response contains all event fields
- **EventCreateToolTests**: verifies EventCreateRequest is passed through, verifies response contains created event ID
- **EventUpdateToolTests**: verifies eventId and EventUpdateRequest are passed through, verifies patch semantics
- **EventDeleteToolTests**: verifies provider.DeleteEventAsync is called, verifies confirmation response
- **CheckAvailabilityToolTests**: verifies date range passed to provider, verifies FreeBusySlot mapping to JSON

**Note:** The `Run` method is `protected` in domain tools. Use testable subclasses (as shown above) or make Run accessible via `InternalsVisibleTo` (already set for Tests project in Infrastructure — add it to Domain.csproj too if needed).

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Domain.Calendar"
```
Expected: ALL tests FAIL (classes `CalendarListTool`, `EventListTool`, etc. do not exist yet).

**Commit:** `git commit -m "test: add failing tests for calendar domain tools"`

---

### Task 1.2: Implement Domain Calendar Tools

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.1 must be complete

**Goal:** Write the minimal code to make ALL tests from Task 1.1 pass. Follow existing pattern from `Domain/Tools/Memory/MemoryStoreTool.cs`.

**Files:**
- Create: `Domain/Tools/Calendar/CalendarListTool.cs`
- Create: `Domain/Tools/Calendar/EventListTool.cs`
- Create: `Domain/Tools/Calendar/EventGetTool.cs`
- Create: `Domain/Tools/Calendar/EventCreateTool.cs`
- Create: `Domain/Tools/Calendar/EventUpdateTool.cs`
- Create: `Domain/Tools/Calendar/EventDeleteTool.cs`
- Create: `Domain/Tools/Calendar/CheckAvailabilityTool.cs`

**Implementation pattern** (all 7 follow this structure):

```csharp
// Domain/Tools/Calendar/CalendarListTool.cs
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Calendar;

public class CalendarListTool(ICalendarProvider provider)
{
    public const string ToolName = "calendar_list";

    public const string ToolDescription = """
        Lists all calendars available for the authenticated user.
        Returns calendar IDs, names, whether each is the default, and edit permissions.
        """;

    protected async Task<JsonNode> Run(string accessToken, CancellationToken ct = default)
    {
        var calendars = await provider.ListCalendarsAsync(accessToken, ct);
        return new JsonArray(calendars.Select(c => new JsonObject
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["color"] = c.Color,
            ["isDefault"] = c.IsDefault,
            ["canEdit"] = c.CanEdit
        }).ToArray<JsonNode>());
    }
}
```

Each tool:
1. Takes `ICalendarProvider` via primary constructor
2. Exposes `ToolName` and `ToolDescription` as public constants
3. Has `protected async Task<JsonNode> Run(...)` that delegates to the corresponding provider method
4. Formats the result as `JsonNode` (JsonObject for single items, JsonArray for lists)
5. No try/catch — errors propagate to the MCP filter

For **EventDeleteTool**, return a confirmation JsonObject: `{ "status": "deleted", "eventId": "..." }`.

For **EventCreateTool** and **EventUpdateTool**, return the full event as JsonObject.

For **CheckAvailabilityTool**, return a JsonArray of free/busy slots: `{ "start": "...", "end": "...", "status": "Busy" }`.

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Domain.Calendar"
```
Expected: ALL tests PASS.

**Commit:** `git commit -m "feat: implement calendar domain tools"`

---

### Task 1.3: Adversarial review of Domain Calendar Tools

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.2 must be complete

**Your role:** Adversarial reviewer. Try to BREAK the implementation.

**Design requirements to verify:**
- Each of the 7 domain tools correctly delegates to the corresponding `ICalendarProvider` method
- Each tool has `ToolName` and `ToolDescription` constants (non-empty)
- Each tool formats responses as `JsonNode` with all DTO fields included
- EventDeleteTool returns confirmation (not void)
- EventUpdateTool uses patch semantics (all request fields optional)
- No Domain layer violations (no Infrastructure imports, no HttpClient, no concrete implementations)

**Review checklist:**

1. **Design compliance** — Read each tool implementation, verify all 7 provider methods are covered, verify response format includes all DTO fields.

2. **Test adequacy** — Could tests pass with a wrong implementation? For example, do tests verify that the *correct* provider method is called with the *correct* arguments?

3. **Edge cases** — What happens with null accessToken? Empty string? What happens if the provider throws?

4. **Domain purity** — Verify no imports from Infrastructure or Agent namespaces.

5. **Pattern consistency** — Compare with `Domain/Tools/Memory/MemoryStoreTool.cs`. Are constants, method signatures, and response formatting consistent?

**You MUST write and run at least 3 additional tests.** Suggestions:
- Test that provider exceptions propagate (not swallowed)
- Test response JSON includes ALL DTO fields (not just a subset)
- Test EventUpdateTool handles empty/null update request gracefully

**What to produce:**
- List of issues (Critical / Important / Minor)
- Additional tests and results
- Verdict: PASS or FAIL

**If FAIL:** Create fix tasks.

**Commit additional tests:** `git commit -m "test: add adversarial tests for calendar domain tools"`

---

## Feature 2: RedisCalendarTokenStore

### Task 2.1: Write failing tests for RedisCalendarTokenStore

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 0

**Design requirements being tested:**
- Implements `ICalendarTokenStore`
- Stores encrypted tokens in Redis as JSON, keyed by `calendar:tokens:{userId}`
- `GetTokensAsync` returns null if no tokens exist
- `StoreTokensAsync` creates/overwrites tokens with TTL
- `RemoveTokensAsync` deletes the key
- `HasTokensAsync` returns true/false based on key existence
- Tokens are encrypted at rest (DataProtection API or similar)

**Files:**
- Create: `Tests/Unit/Infrastructure/Calendar/RedisCalendarTokenStoreTests.cs`

**What to test:**

Use Moq to mock `IDatabase` (from StackExchange.Redis) and `IDataProtector` (from `Microsoft.AspNetCore.DataProtection`). Follow existing pattern from `Tests/Unit/Infrastructure/Memory/`.

```csharp
// Tests/Unit/Infrastructure/Calendar/RedisCalendarTokenStoreTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Calendar;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Calendar;

public class RedisCalendarTokenStoreTests
{
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly RedisCalendarTokenStore _store;

    public RedisCalendarTokenStoreTests()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_dbMock.Object);
        _store = new RedisCalendarTokenStore(redisMock.Object);
    }

    [Fact]
    public async Task StoreTokensAsync_WritesToRedisWithCorrectKey()
    {
        var tokens = new OAuthTokens
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await _store.StoreTokensAsync("user-1", tokens);

        _dbMock.Verify(db => db.StringSetAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task GetTokensAsync_WhenKeyExists_ReturnsTokens()
    {
        // Arrange: mock Redis to return serialized+encrypted token JSON
        // The exact setup depends on encryption implementation
        _dbMock.Setup(db => db.StringGetAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(/* encrypted JSON */));

        var result = await _store.GetTokensAsync("user-1");

        result.ShouldNotBeNull();
        result.AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetTokensAsync_WhenKeyDoesNotExist_ReturnsNull()
    {
        _dbMock.Setup(db => db.StringGetAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var result = await _store.GetTokensAsync("user-1");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RemoveTokensAsync_DeletesKey()
    {
        await _store.RemoveTokensAsync("user-1");

        _dbMock.Verify(db => db.KeyDeleteAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task HasTokensAsync_WhenKeyExists_ReturnsTrue()
    {
        _dbMock.Setup(db => db.KeyExistsAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var result = await _store.HasTokensAsync("user-1");

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task HasTokensAsync_WhenKeyDoesNotExist_ReturnsFalse()
    {
        _dbMock.Setup(db => db.KeyExistsAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);

        var result = await _store.HasTokensAsync("user-1");

        result.ShouldBeFalse();
    }
}
```

**Note:** The encryption mechanism (DataProtection, AES, etc.) will affect exact mock setup. The tests above verify the Redis interactions; encryption tests may need adjustment once the implementation approach is chosen. At minimum, verify that what's stored in Redis is NOT plain-text tokens.

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Infrastructure.Calendar.RedisCalendarTokenStore"
```
Expected: ALL tests FAIL (`RedisCalendarTokenStore` class doesn't exist).

**Commit:** `git commit -m "test: add failing tests for RedisCalendarTokenStore"`

---

### Task 2.2: Implement RedisCalendarTokenStore

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.1 must be complete

**Goal:** Implement `ICalendarTokenStore` with Redis persistence and token encryption.

**Files:**
- Create: `Infrastructure/Calendar/RedisCalendarTokenStore.cs`

**Implementation:**

```csharp
// Infrastructure/Calendar/RedisCalendarTokenStore.cs
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.Calendar;

public class RedisCalendarTokenStore(IConnectionMultiplexer redis) : ICalendarTokenStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(90);
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<OAuthTokens?> GetTokensAsync(string userId, CancellationToken ct = default)
    {
        var value = await _db.StringGetAsync(Key(userId));
        if (value.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<OAuthTokens>(value.ToString());
    }

    public async Task StoreTokensAsync(string userId, OAuthTokens tokens, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(tokens);
        await _db.StringSetAsync(Key(userId), json, DefaultTtl);
    }

    public async Task RemoveTokensAsync(string userId, CancellationToken ct = default)
    {
        await _db.KeyDeleteAsync(Key(userId));
    }

    public async Task<bool> HasTokensAsync(string userId, CancellationToken ct = default)
    {
        return await _db.KeyExistsAsync(Key(userId));
    }

    private static string Key(string userId) => $"calendar:tokens:{userId}";
}
```

**Note on encryption:** The initial implementation may store as plain JSON for simplicity. Add DataProtection encryption as a follow-up if the review requires it. The key concern is that Redis itself should be in a protected network (which it is — Docker internal network). If encryption is required, wrap serialize/deserialize with `IDataProtector.Protect`/`Unprotect`.

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Infrastructure.Calendar.RedisCalendarTokenStore"
```
Expected: ALL tests PASS.

**Commit:** `git commit -m "feat: implement RedisCalendarTokenStore"`

---

### Task 2.3: Adversarial review of RedisCalendarTokenStore

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.2 must be complete

**Your role:** Adversarial reviewer. Try to BREAK the implementation.

**Design requirements to verify:**
- Implements `ICalendarTokenStore` (all 4 methods)
- Key pattern: `calendar:tokens:{userId}`
- Tokens stored with TTL (aligned with refresh token lifetime)
- GetTokens returns null when key doesn't exist
- StoreTokens creates/overwrites
- RemoveTokens deletes the key
- HasTokens checks key existence

**Review checklist:**

1. **Design compliance** — All 4 interface methods implemented? Key pattern correct? TTL set?
2. **Test adequacy** — Do tests verify serialization round-trip (store → get returns same values)?
3. **Edge cases** — What happens with empty userId? Null tokens? Corrupted Redis data?
4. **Security** — Are tokens stored in plain text? Is this acceptable given the Docker network isolation?
5. **Thread safety** — Is the implementation safe for concurrent access?

**You MUST write and run at least 3 additional tests:**
- Serialization round-trip: store tokens, retrieve them, verify all fields match
- Store overwrites existing tokens for same userId
- Handle malformed Redis data gracefully (don't crash)

**Commit additional tests:** `git commit -m "test: add adversarial tests for RedisCalendarTokenStore"`

---

## Feature 3: MicrosoftGraphCalendarProvider

### Task 3.1: Write failing tests for MicrosoftGraphCalendarProvider

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 0

**Design requirements being tested:**
- Implements `ICalendarProvider`
- Uses HttpClient to call Microsoft Graph REST API (`https://graph.microsoft.com/v1.0/`)
- Sets Authorization header with access token per-request (thread-safe)
- Maps Graph API JSON responses to Domain DTOs
- ListCalendars calls `GET /me/calendars`
- ListEvents calls `GET /me/calendars/{id}/events` or `GET /me/events` with date filter
- GetEvent calls `GET /me/events/{id}`
- CreateEvent calls `POST /me/calendars/{id}/events` or `POST /me/events`
- UpdateEvent calls `PATCH /me/events/{id}`
- DeleteEvent calls `DELETE /me/events/{id}`
- CheckAvailability calls `POST /me/calendar/getSchedule`

**Files:**
- Create: `Tests/Unit/Infrastructure/Calendar/MicrosoftGraphCalendarProviderTests.cs`

**What to test:**

Use WireMock.Net to mock the Graph API (same pattern as `Tests/Unit/Infrastructure/Memory/OpenRouterEmbeddingServiceMockTests.cs`).

```csharp
// Tests/Unit/Infrastructure/Calendar/MicrosoftGraphCalendarProviderTests.cs
using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Calendar;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure.Calendar;

public class MicrosoftGraphCalendarProviderTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly MicrosoftGraphCalendarProvider _provider;

    public MicrosoftGraphCalendarProviderTests()
    {
        _server = WireMockServer.Start();
        var httpClient = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _provider = new MicrosoftGraphCalendarProvider(httpClient);
    }

    [Fact]
    public async Task ListCalendarsAsync_CallsCorrectEndpoint_ReturnsMappedCalendars()
    {
        var graphResponse = new
        {
            value = new[]
            {
                new { id = "cal-1", name = "Calendar", color = "auto", isDefaultCalendar = true, canEdit = true },
                new { id = "cal-2", name = "Work", color = "lightBlue", isDefaultCalendar = false, canEdit = true }
            }
        };

        _server.Given(Request.Create().WithPath("/me/calendars").UsingGet()
                .WithHeader("Authorization", "Bearer test-token"))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        var result = await _provider.ListCalendarsAsync("test-token");

        result.Count.ShouldBe(2);
        result[0].Id.ShouldBe("cal-1");
        result[0].Name.ShouldBe("Calendar");
        result[0].IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task ListEventsAsync_WithCalendarId_CallsCalendarSpecificEndpoint()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddDays(7);
        var graphResponse = new
        {
            value = new[]
            {
                new
                {
                    id = "evt-1",
                    subject = "Team Standup",
                    body = new { content = "Daily standup" },
                    start = new { dateTime = start.AddHours(1).ToString("o"), timeZone = "UTC" },
                    end = new { dateTime = start.AddHours(1.5).ToString("o"), timeZone = "UTC" },
                    location = new { displayName = "Room A" },
                    isAllDay = false,
                    attendees = new[] { new { emailAddress = new { address = "bob@example.com" } } },
                    organizer = new { emailAddress = new { address = "alice@example.com" } }
                }
            }
        };

        _server.Given(Request.Create().WithPath("/me/calendars/cal-1/events").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        var result = await _provider.ListEventsAsync("test-token", "cal-1", start, end);

        result.Count.ShouldBe(1);
        result[0].Subject.ShouldBe("Team Standup");
        result[0].Location.ShouldBe("Room A");
        result[0].Attendees.ShouldContain("bob@example.com");
    }

    [Fact]
    public async Task CreateEventAsync_PostsToCorrectEndpoint_ReturnsCreatedEvent()
    {
        var request = new EventCreateRequest
        {
            Subject = "New Meeting",
            Start = DateTimeOffset.UtcNow.AddDays(1),
            End = DateTimeOffset.UtcNow.AddDays(1).AddHours(1)
        };

        var graphResponse = new
        {
            id = "evt-new",
            subject = "New Meeting",
            start = new { dateTime = request.Start.ToString("o"), timeZone = "UTC" },
            end = new { dateTime = request.End.ToString("o"), timeZone = "UTC" }
        };

        _server.Given(Request.Create().WithPath("/me/events").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(graphResponse)));

        var result = await _provider.CreateEventAsync("test-token", request);

        result.Id.ShouldBe("evt-new");
        result.Subject.ShouldBe("New Meeting");
    }

    [Fact]
    public async Task DeleteEventAsync_CallsDeleteEndpoint()
    {
        _server.Given(Request.Create().WithPath("/me/events/evt-1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        await _provider.DeleteEventAsync("test-token", "evt-1", null);

        // Verify no exception thrown and correct endpoint called
        _server.LogEntries.ShouldContain(e => e.RequestMessage.Path == "/me/events/evt-1");
    }

    [Fact]
    public async Task ListCalendarsAsync_OnHttpError_ThrowsException()
    {
        _server.Given(Request.Create().WithPath("/me/calendars").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("Unauthorized"));

        await Should.ThrowAsync<HttpRequestException>(() =>
            _provider.ListCalendarsAsync("bad-token"));
    }

    public void Dispose() => _server.Dispose();
}
```

Add similar tests for `GetEventAsync`, `UpdateEventAsync` (PATCH), and `CheckAvailabilityAsync` (POST to `/me/calendar/getSchedule`).

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Infrastructure.Calendar.MicrosoftGraphCalendarProvider"
```
Expected: ALL tests FAIL (`MicrosoftGraphCalendarProvider` class doesn't exist).

**Commit:** `git commit -m "test: add failing tests for MicrosoftGraphCalendarProvider"`

---

### Task 3.2: Implement MicrosoftGraphCalendarProvider

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 3.1 must be complete

**Goal:** Implement `ICalendarProvider` using HttpClient against Microsoft Graph REST API.

**Files:**
- Create: `Infrastructure/Calendar/MicrosoftGraphCalendarProvider.cs`

**Implementation pattern:**

```csharp
// Infrastructure/Calendar/MicrosoftGraphCalendarProvider.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Infrastructure.Calendar;

public class MicrosoftGraphCalendarProvider(HttpClient httpClient) : ICalendarProvider
{
    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "me/calendars", accessToken);
        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GraphListResponse<GraphCalendar>>(ct);
        return result?.Value.Select(MapCalendar).ToList() ?? [];
    }

    // ... remaining 6 methods following the same pattern

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static CalendarInfo MapCalendar(GraphCalendar g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Color = g.Color,
        IsDefault = g.IsDefaultCalendar,
        CanEdit = g.CanEdit
    };

    // Internal Graph API DTOs for deserialization
    // Follow existing pattern from OpenRouterEmbeddingService
}
```

Key implementation notes:
- Each method creates a new `HttpRequestMessage` with Bearer token (thread-safe, no shared headers)
- Internal `Graph*` record types for JSON deserialization of Graph API responses
- Map Graph models to Domain DTOs in static mapping methods
- For `CreateEvent`/`UpdateEvent`: serialize request body as Graph API format (different property names than Domain DTOs)
- For `CheckAvailability`: POST to `/me/calendar/getSchedule` with schedule request body
- Use `[JsonPropertyName]` attributes on internal DTOs as needed

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Infrastructure.Calendar.MicrosoftGraphCalendarProvider"
```
Expected: ALL tests PASS.

**Commit:** `git commit -m "feat: implement MicrosoftGraphCalendarProvider"`

---

### Task 3.3: Adversarial review of MicrosoftGraphCalendarProvider

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 3.2 must be complete

**Your role:** Adversarial reviewer. Try to BREAK the implementation.

**Design requirements to verify:**
- Implements all 7 `ICalendarProvider` methods
- Uses correct Graph API endpoints for each operation
- Sets Authorization header per-request (not on DefaultRequestHeaders)
- Maps all DTO fields correctly (no missing fields)
- Handles HTTP errors by throwing (EnsureSuccessStatusCode)
- Graph API date formats handled correctly (ISO 8601 with timezone)

**Review checklist:**

1. **API correctness** — Verify each method calls the correct Graph endpoint with correct HTTP method
2. **DTO mapping** — Verify all CalendarEvent fields are mapped from Graph response (especially Attendees, Recurrence)
3. **Thread safety** — Verify no shared mutable state (no DefaultRequestHeaders mutation)
4. **Date handling** — Graph API returns `{ dateTime, timeZone }` objects. Verify correct parsing to DateTimeOffset
5. **Request bodies** — Verify CreateEvent/UpdateEvent serialize correctly for Graph API format

**You MUST write and run at least 3 additional tests:**
- Test `UpdateEventAsync` sends PATCH with only the provided fields (patch semantics)
- Test `CheckAvailabilityAsync` maps free/busy status correctly (Free, Busy, Tentative, OOF)
- Test `ListEventsAsync` without calendarId uses `/me/events` endpoint (not `/me/calendars/null/events`)

**Commit additional tests:** `git commit -m "test: add adversarial tests for MicrosoftGraphCalendarProvider"`

---

## Feature 4: MCP Calendar Tools + Server Wiring

### Task 4.1: Write failing tests for MCP Calendar Tools

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Feature 1 (Task 1.3 PASS) — domain tools must exist

**Design requirements being tested:**
- 7 MCP tool classes in `McpServerCalendar/McpTools/`, each extending the corresponding domain tool
- Each decorated with `[McpServerToolType]` and `[McpServerTool]`
- Each `McpRun` method delegates to parent `Run()` and wraps in `ToolResponse.Create()`
- Parameters have `[Description]` attributes for MCP schema
- `accessToken` parameter on every tool
- ConfigModule registers all tools and `ICalendarProvider` as `MicrosoftGraphCalendarProvider`
- Global error filter catches exceptions and returns `ToolResponse.Create(ex)`

**Files:**
- Create: `Tests/Unit/McpServerCalendar/McpCalendarToolTests.cs`

**What to test:**

```csharp
// Tests/Unit/McpServerCalendar/McpCalendarToolTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Utils;
using McpServerCalendar.McpTools;
using ModelContextProtocol.Protocol;
using Moq;
using Shouldly;

namespace Tests.Unit.McpServerCalendar;

public class McpCalendarToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();

    [Fact]
    public async Task McpCalendarListTool_DelegatesToProviderAndReturnsCallToolResult()
    {
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-1", Name = "Personal", IsDefault = true, CanEdit = true }
        };
        _providerMock.Setup(p => p.ListCalendarsAsync("token-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        var tool = new McpCalendarListTool(_providerMock.Object);
        var result = await tool.McpRun("token-1");

        result.ShouldNotBeNull();
        result.IsError.ShouldBeFalse();
        result.Content.ShouldNotBeEmpty();
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("cal-1");
        text.ShouldContain("Personal");
    }

    [Fact]
    public async Task McpEventCreateTool_DelegatesToProviderAndReturnsCreatedEvent()
    {
        var created = new CalendarEvent
        {
            Id = "evt-new",
            Subject = "Meeting",
            Start = DateTimeOffset.UtcNow.AddDays(1),
            End = DateTimeOffset.UtcNow.AddDays(1).AddHours(1)
        };
        _providerMock.Setup(p => p.CreateEventAsync("token", It.IsAny<EventCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);

        var tool = new McpEventCreateTool(_providerMock.Object);
        var result = await tool.McpRun("token", "Meeting",
            DateTimeOffset.UtcNow.AddDays(1).ToString("o"),
            DateTimeOffset.UtcNow.AddDays(1).AddHours(1).ToString("o"));

        result.IsError.ShouldBeFalse();
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        text.ShouldContain("evt-new");
    }

    [Fact]
    public async Task McpEventDeleteTool_DelegatesToProvider()
    {
        _providerMock.Setup(p => p.DeleteEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tool = new McpEventDeleteTool(_providerMock.Object);
        var result = await tool.McpRun("token", "evt-1");

        result.IsError.ShouldBeFalse();
        _providerMock.Verify(p => p.DeleteEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

Write similar tests for all 7 MCP tools: `McpCalendarListTool`, `McpEventListTool`, `McpEventGetTool`, `McpEventCreateTool`, `McpEventUpdateTool`, `McpEventDeleteTool`, `McpCheckAvailabilityTool`.

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpServerCalendar"
```
Expected: ALL tests FAIL (MCP tool classes don't exist).

**Commit:** `git commit -m "test: add failing tests for MCP calendar tools"`

---

### Task 4.2: Implement MCP Calendar Tools + Server Wiring

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 4.1 must be complete

**Goal:** Create 7 MCP tool wrappers and wire up ConfigModule with full DI registration.

**Files:**
- Create: `McpServerCalendar/McpTools/McpCalendarListTool.cs`
- Create: `McpServerCalendar/McpTools/McpEventListTool.cs`
- Create: `McpServerCalendar/McpTools/McpEventGetTool.cs`
- Create: `McpServerCalendar/McpTools/McpEventCreateTool.cs`
- Create: `McpServerCalendar/McpTools/McpEventUpdateTool.cs`
- Create: `McpServerCalendar/McpTools/McpEventDeleteTool.cs`
- Create: `McpServerCalendar/McpTools/McpCheckAvailabilityTool.cs`
- Modify: `McpServerCalendar/Modules/ConfigModule.cs` (full implementation)

**Implementation pattern** (follow `McpServerMemory/McpTools/McpMemoryStoreTool.cs`):

```csharp
// McpServerCalendar/McpTools/McpCalendarListTool.cs
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Calendar;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCalendar.McpTools;

[McpServerToolType]
public class McpCalendarListTool(ICalendarProvider provider)
    : CalendarListTool(provider)
{
    [McpServerTool(Name = ToolName)]
    [Description(ToolDescription)]
    public async Task<CallToolResult> McpRun(
        [Description("Access token for calendar authentication")]
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var result = await Run(accessToken, cancellationToken);
        return ToolResponse.Create(result);
    }
}
```

For tools with more parameters (EventCreate, EventList, etc.), expose each parameter with `[Description]` attributes. Parse string dates to `DateTimeOffset` where needed.

**ConfigModule** — full implementation:

```csharp
public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
{
    services.AddHttpClient<ICalendarProvider, MicrosoftGraphCalendarProvider>(httpClient =>
    {
        httpClient.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
    });

    services
        .AddMcpServer()
        .WithHttpTransport()
        .AddCallToolFilter(next => async (context, cancellationToken) =>
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
        })
        .WithTools<McpCalendarListTool>()
        .WithTools<McpEventListTool>()
        .WithTools<McpEventGetTool>()
        .WithTools<McpEventCreateTool>()
        .WithTools<McpEventUpdateTool>()
        .WithTools<McpEventDeleteTool>()
        .WithTools<McpCheckAvailabilityTool>();

    return services;
}
```

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpServerCalendar"
```
Expected: ALL tests PASS.

**Commit:** `git commit -m "feat: implement MCP calendar tools and server wiring"`

---

### Task 4.3: Adversarial review of MCP Calendar Tools

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 4.2 must be complete

**Your role:** Adversarial reviewer. Try to BREAK the implementation.

**Design requirements to verify:**
- 7 MCP tool classes, each extending the correct domain tool
- `[McpServerToolType]`, `[McpServerTool]`, `[Description]` attributes present
- `McpRun` delegates to `Run()` and wraps with `ToolResponse.Create()`
- All tool parameters have `[Description]` attributes
- ConfigModule registers all 7 tools
- ConfigModule registers `ICalendarProvider` as `MicrosoftGraphCalendarProvider`
- Global error filter present (catches exceptions, logs, returns error response)
- No try/catch in individual tool methods (handled by filter)

**Review checklist:**

1. **Pattern compliance** — Compare each MCP tool with `McpMemoryStoreTool`. Same attribute pattern? Same delegation pattern?
2. **Parameter coverage** — Does each MCP tool expose all parameters from the design? (e.g., EventCreate should have subject, start, end, calendarId?, location?, body?, attendees?, isAllDay?, recurrence?)
3. **Date parsing** — MCP tools receive dates as strings from LLM. Are they parsed to DateTimeOffset correctly?
4. **ConfigModule** — All 7 tools registered? Error filter present? HttpClient base address correct?

**You MUST write and run at least 3 additional tests:**
- Test that invalid date string in McpEventListTool produces a meaningful error (not a crash)
- Test that McpEventUpdateTool correctly passes null/empty optional parameters
- Verify ConfigModule can be called without throwing (basic DI wiring test)

**Commit additional tests:** `git commit -m "test: add adversarial tests for MCP calendar tools"`

---

## Feature 5: Agent OAuth Endpoints

### Task 5.1: Write failing tests for Agent OAuth Endpoints

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Feature 2 (Task 2.3 PASS) — RedisCalendarTokenStore must exist

**Design requirements being tested:**
- Agent exposes `/auth/microsoft/authorize` — redirects to Microsoft OAuth consent page
- Agent exposes `/auth/microsoft/callback` — exchanges auth code for tokens, stores in Redis
- Agent exposes `/auth/status/{userId}` — returns whether user has connected calendar
- Agent exposes `/auth/disconnect/{userId}` — removes tokens from Redis
- Authorization uses PKCE (Proof Key for Code Exchange)
- Uses MSAL (`Microsoft.Identity.Client`) for token exchange
- Tokens stored via `ICalendarTokenStore`

**Files:**
- Create: `Tests/Unit/Agent/CalendarAuthEndpointTests.cs`

**What to test:**

The Agent project uses minimal APIs or controllers. Test the endpoint logic (not HTTP routing) by testing the service/handler that backs the endpoints. Use Moq for `ICalendarTokenStore` and MSAL abstractions.

```csharp
// Tests/Unit/Agent/CalendarAuthEndpointTests.cs
namespace Tests.Unit.Agent;

public class CalendarAuthEndpointTests
{
    private readonly Mock<ICalendarTokenStore> _tokenStoreMock = new();

    [Fact]
    public async Task AuthStatus_WhenTokensExist_ReturnsConnected()
    {
        _tokenStoreMock.Setup(s => s.HasTokensAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Test the handler/service method directly
        // var result = await handler.GetStatus("user-1");
        // result.Connected.ShouldBeTrue();
    }

    [Fact]
    public async Task AuthStatus_WhenNoTokens_ReturnsNotConnected()
    {
        _tokenStoreMock.Setup(s => s.HasTokensAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // var result = await handler.GetStatus("user-1");
        // result.Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task Disconnect_RemovesTokensFromStore()
    {
        // await handler.Disconnect("user-1");
        // _tokenStoreMock.Verify(s => s.RemoveTokensAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Callback_WithValidCode_StoresTokens()
    {
        // Mock MSAL to return tokens for a valid auth code
        // await handler.HandleCallback("valid-code", "state-with-userId");
        // _tokenStoreMock.Verify(s => s.StoreTokensAsync("user-1", It.IsAny<OAuthTokens>(), ...), Times.Once);
    }

    [Fact]
    public void Authorize_GeneratesCorrectRedirectUrl()
    {
        // var url = handler.GetAuthorizationUrl("user-1", "https://callback-url");
        // url.ShouldContain("login.microsoftonline.com");
        // url.ShouldContain("Calendars.ReadWrite");
        // url.ShouldContain("code_challenge"); // PKCE
    }
}
```

**Note:** The exact test structure depends on how the Agent implements the endpoints (minimal API handlers, controller methods, or a separate service). The tests should focus on the *logic* rather than HTTP routing. Structure tests around a `CalendarAuthService` or similar that the endpoints delegate to.

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Agent.CalendarAuth"
```
Expected: ALL tests FAIL.

**Commit:** `git commit -m "test: add failing tests for Agent OAuth endpoints"`

---

### Task 5.2: Implement Agent OAuth Endpoints

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 5.1 must be complete

**Goal:** Add OAuth endpoints to the Agent backend and wire up `ICalendarTokenStore`.

**Files:**
- Create: `Infrastructure/Calendar/CalendarAuthService.cs` — OAuth logic (MSAL wrapper)
- Modify: `Agent/Program.cs` or appropriate startup file — register endpoints
- Modify: `Agent/Modules/InjectorModule.cs` — register `ICalendarTokenStore` and `CalendarAuthService`
- Create: `Agent/Settings/MicrosoftAuthSettings.cs` — config for client ID, secret, tenant
- Modify: `Agent/appsettings.json` — add Microsoft auth config section
- Modify: `DockerCompose/docker-compose.yml` — add MICROSOFT__ env vars to agent service

**Implementation outline:**

`CalendarAuthService` handles:
1. `GetAuthorizationUrl(userId, redirectUri)` — builds Microsoft OAuth URL with PKCE
2. `HandleCallbackAsync(code, state)` — exchanges code for tokens via MSAL, stores in Redis
3. `GetStatusAsync(userId)` — checks if user has tokens
4. `DisconnectAsync(userId)` — removes tokens

Agent endpoints (minimal API):
```csharp
app.MapGet("/auth/microsoft/authorize", (string userId, CalendarAuthService auth) => ...);
app.MapGet("/auth/microsoft/callback", (string code, string state, CalendarAuthService auth) => ...);
app.MapGet("/auth/status/{userId}", (string userId, CalendarAuthService auth) => ...);
app.MapPost("/auth/disconnect/{userId}", (string userId, CalendarAuthService auth) => ...);
```

**Package additions:** Add `Microsoft.Identity.Client` to `Infrastructure.csproj` for MSAL.

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.Agent.CalendarAuth"
```
Expected: ALL tests PASS.

**Commit:** `git commit -m "feat: implement Agent OAuth endpoints for calendar authentication"`

---

### Task 5.3: Adversarial review of Agent OAuth Endpoints

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 5.2 must be complete

**Your role:** Adversarial reviewer. Try to BREAK the implementation.

**Design requirements to verify:**
- 4 endpoints: authorize, callback, status, disconnect
- PKCE used in authorization flow
- MSAL handles token exchange
- Tokens stored via ICalendarTokenStore (not directly in Redis)
- Callback validates state parameter (prevents CSRF)
- Microsoft auth settings read from configuration (client ID, secret, tenant)
- Environment variables added to docker-compose and .env

**Review checklist:**

1. **Security** — Is PKCE implemented correctly? Is state parameter validated on callback? Are redirect URIs validated?
2. **Configuration** — Are MICROSOFT__ env vars in docker-compose.yml, .env, and appsettings.json? (CLAUDE.md rule)
3. **Error handling** — What happens if MSAL token exchange fails? Invalid auth code? Expired code?
4. **DI wiring** — Is ICalendarTokenStore registered in Agent's DI? Is CalendarAuthService registered?

**You MUST write and run at least 3 additional tests:**
- Test callback with invalid/missing state parameter → appropriate error
- Test callback with expired/invalid auth code → appropriate error
- Test disconnect for non-existent user → no-op, no error

**Commit additional tests:** `git commit -m "test: add adversarial tests for Agent OAuth endpoints"`

---

## Feature 6: WebChat Connected Accounts

### Task 6.1: Write failing tests for WebChat Connected Accounts

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Feature 5 (Task 5.3 PASS) — Agent OAuth endpoints must exist

**Design requirements being tested:**
- `ConnectedAccountsState` record with per-provider connection status
- Actions: `AccountConnected`, `AccountDisconnected`, `AccountStatusLoaded`
- Reducers update state immutably
- `ConnectedAccountsStore` wires actions to store via Dispatcher
- Store hydrates by checking Agent's `/auth/status/{userId}` endpoint

**Files:**
- Create: `Tests/Unit/WebChat/State/ConnectedAccounts/ConnectedAccountsReducerTests.cs`
- Create: `Tests/Unit/WebChat/State/ConnectedAccounts/ConnectedAccountsStoreTests.cs`

**What to test:**

```csharp
// Tests/Unit/WebChat/State/ConnectedAccounts/ConnectedAccountsReducerTests.cs
using Shouldly;
using WebChat.Client.State.ConnectedAccounts;

namespace Tests.Unit.WebChat.State.ConnectedAccounts;

public class ConnectedAccountsReducerTests
{
    [Fact]
    public void AccountStatusLoaded_SetsProviderStatus()
    {
        var state = ConnectedAccountsState.Initial;
        var action = new AccountStatusLoaded("microsoft", true, "user@example.com");

        var newState = ConnectedAccountsReducers.Reduce(state, action);

        newState.Providers["microsoft"].Connected.ShouldBeTrue();
        newState.Providers["microsoft"].Email.ShouldBe("user@example.com");
    }

    [Fact]
    public void AccountDisconnected_ClearsProviderStatus()
    {
        var state = ConnectedAccountsState.Initial with
        {
            Providers = new Dictionary<string, ProviderStatus>
            {
                ["microsoft"] = new(true, "user@example.com")
            }
        };
        var action = new AccountDisconnected("microsoft");

        var newState = ConnectedAccountsReducers.Reduce(state, action);

        newState.Providers["microsoft"].Connected.ShouldBeFalse();
    }

    [Fact]
    public void Initial_HasNoConnectedProviders()
    {
        var state = ConnectedAccountsState.Initial;
        state.Providers.ShouldBeEmpty();
    }
}
```

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.WebChat.State.ConnectedAccounts"
```
Expected: ALL tests FAIL.

**Commit:** `git commit -m "test: add failing tests for WebChat ConnectedAccountsStore"`

---

### Task 6.2: Implement WebChat Connected Accounts

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 6.1 must be complete

**Goal:** Implement the state management for connected accounts and the UI component.

**Files:**
- Create: `WebChat.Client/State/ConnectedAccounts/ConnectedAccountsState.cs`
- Create: `WebChat.Client/State/ConnectedAccounts/ConnectedAccountsActions.cs`
- Create: `WebChat.Client/State/ConnectedAccounts/ConnectedAccountsReducers.cs`
- Create: `WebChat.Client/State/ConnectedAccounts/ConnectedAccountsStore.cs`
- Create: `WebChat.Client/Components/ConnectedAccounts.razor` (basic UI component)
- Modify: `WebChat.Client/Extensions/ServiceCollectionExtensions.cs` (register store)

**Implementation pattern** (follow existing stores like `MessagesStore`):

State:
```csharp
public sealed record ConnectedAccountsState
{
    public IReadOnlyDictionary<string, ProviderStatus> Providers { get; init; }
        = new Dictionary<string, ProviderStatus>();

    public static ConnectedAccountsState Initial => new();
}

public record ProviderStatus(bool Connected, string? Email = null);
```

Actions:
```csharp
public record AccountStatusLoaded(string Provider, bool Connected, string? Email) : IAction;
public record AccountConnected(string Provider, string? Email) : IAction;
public record AccountDisconnected(string Provider) : IAction;
```

Store:
```csharp
public sealed class ConnectedAccountsStore : IDisposable
{
    private readonly Store<ConnectedAccountsState> _store;

    public ConnectedAccountsStore(Dispatcher dispatcher)
    {
        _store = new Store<ConnectedAccountsState>(ConnectedAccountsState.Initial);
        dispatcher.RegisterHandler<AccountStatusLoaded>(action => _store.Dispatch(action, ConnectedAccountsReducers.Reduce));
        dispatcher.RegisterHandler<AccountConnected>(action => _store.Dispatch(action, ConnectedAccountsReducers.Reduce));
        dispatcher.RegisterHandler<AccountDisconnected>(action => _store.Dispatch(action, ConnectedAccountsReducers.Reduce));
    }

    public ConnectedAccountsState State => _store.State;
    public IObservable<ConnectedAccountsState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();
}
```

UI Component — a Blazor component showing "Outlook Calendar: Connected / Not Connected" with Connect/Disconnect buttons. The Connect button opens a popup to `/auth/microsoft/authorize`. The Disconnect button calls `/auth/disconnect/{userId}`. Keep it minimal.

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.WebChat.State.ConnectedAccounts"
```
Expected: ALL tests PASS.

**Commit:** `git commit -m "feat: implement WebChat ConnectedAccountsStore and UI component"`

---

### Task 6.3: Adversarial review of WebChat Connected Accounts

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 6.2 must be complete

**Your role:** Adversarial reviewer. Try to BREAK the implementation.

**Design requirements to verify:**
- ConnectedAccountsState is immutable record with `Providers` dictionary
- Actions: AccountStatusLoaded, AccountConnected, AccountDisconnected
- Reducers produce new state (no mutation)
- Store registered as scoped in DI (same as other stores)
- UI component shows connection status and has Connect/Disconnect buttons
- Connect opens popup to Agent's OAuth authorize endpoint
- Disconnect calls Agent's disconnect endpoint

**Review checklist:**

1. **Pattern consistency** — Compare with `MessagesStore`. Same registration pattern? Same Dispatcher wiring?
2. **State immutability** — Do reducers create new state or mutate?
3. **UI** — Does the component inject the store and subscribe to state changes?
4. **DI registration** — Is ConnectedAccountsStore added in `ServiceCollectionExtensions`?

**You MUST write and run at least 3 additional tests:**
- Test that multiple AccountStatusLoaded for different providers are independent
- Test AccountConnected updates an already-connected provider (overwrite email)
- Test store dispatches actions correctly through Dispatcher

**Commit additional tests:** `git commit -m "test: add adversarial tests for WebChat ConnectedAccountsStore"`

---

## Feature 7: End-to-End Integration

### Task 7.1: Write failing integration tests

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Features 4, 5, 6 all completed (Task 4.3, 5.3, 6.3 all PASS)

**Design requirements being tested:**
- McpServerCalendar is accessible and lists all 7 tools
- MCP tools can be invoked via MCP client and return correct results
- RedisCalendarTokenStore works with real Redis (store → get round-trip)

**Files:**
- Create: `Tests/Integration/McpServerTests/McpCalendarServerTests.cs`
- Create: `Tests/Integration/Fixtures/McpCalendarServerFixture.cs` (if needed)
- Create: `Tests/Integration/Calendar/RedisCalendarTokenStoreIntegrationTests.cs`

**What to test:**

Follow existing pattern from `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`.

```csharp
// Tests/Integration/McpServerTests/McpCalendarServerTests.cs
namespace Tests.Integration.McpServerTests;

public class McpCalendarServerTests
{
    [Fact]
    public async Task McpServer_ListsAllCalendarTools()
    {
        // Start McpServerCalendar in-process via WebApplicationFactory or similar
        // Create MCP client pointing to the server
        // var tools = await client.ListToolsAsync();
        // tools.Select(t => t.Name).ShouldContain("calendar_list");
        // tools.Select(t => t.Name).ShouldContain("event_list");
        // tools.Select(t => t.Name).ShouldContain("event_get");
        // tools.Select(t => t.Name).ShouldContain("event_create");
        // tools.Select(t => t.Name).ShouldContain("event_update");
        // tools.Select(t => t.Name).ShouldContain("event_delete");
        // tools.Select(t => t.Name).ShouldContain("check_availability");
    }
}

// Tests/Integration/Calendar/RedisCalendarTokenStoreIntegrationTests.cs
// Uses Testcontainers for real Redis
namespace Tests.Integration.Calendar;

public class RedisCalendarTokenStoreIntegrationTests : IAsyncLifetime
{
    // Testcontainers Redis setup

    [Fact]
    public async Task StoreAndRetrieveTokens_RoundTrip()
    {
        // var store = new RedisCalendarTokenStore(redis);
        // var tokens = new OAuthTokens { AccessToken = "a", RefreshToken = "r", ExpiresAt = ... };
        // await store.StoreTokensAsync("user-1", tokens);
        // var retrieved = await store.GetTokensAsync("user-1");
        // retrieved.ShouldNotBeNull();
        // retrieved.AccessToken.ShouldBe("a");
        // retrieved.RefreshToken.ShouldBe("r");
    }
}
```

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Integration.McpServerTests.McpCalendar"
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Integration.Calendar"
```
Expected: Tests FAIL (fixture/server not wired up yet, or compilation fails).

**Commit:** `git commit -m "test: add failing integration tests for calendar MCP server and token store"`

---

### Task 7.2: Fix integration test failures

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 7.1 must be complete

**Goal:** Wire up test fixtures and fix any integration issues so all integration tests pass.

**Files:**
- Create/Modify: `Tests/Integration/Fixtures/McpCalendarServerFixture.cs`
- Possibly modify: McpServerCalendar startup to support in-process testing
- Fix any DI or wiring issues discovered during integration

**Verification:**
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Integration.McpServerTests.McpCalendar"
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Integration.Calendar"
```
Expected: ALL tests PASS.

**Commit:** `git commit -m "feat: wire up calendar integration test fixtures"`

---

### Task 7.3: Final adversarial review

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 7.2 must be complete

**Your role:** Final adversarial reviewer of the ENTIRE calendar feature.

**ALL design requirements checklist (from design document):**

- [ ] New MCP server project `McpServerCalendar` on port 6006
- [ ] 7 MCP tools: CalendarList, EventList, EventGet, EventCreate, EventUpdate, EventDelete, CheckAvailability
- [ ] Domain contracts: `ICalendarProvider`, `ICalendarTokenStore`
- [ ] Domain DTOs: CalendarInfo, CalendarEvent, EventCreateRequest, EventUpdateRequest, FreeBusySlot, OAuthTokens
- [ ] Domain tools: 7 tool classes wrapping ICalendarProvider
- [ ] Infrastructure: `MicrosoftGraphCalendarProvider` implementing ICalendarProvider
- [ ] Infrastructure: `RedisCalendarTokenStore` implementing ICalendarTokenStore
- [ ] Agent OAuth endpoints: authorize, callback, status, disconnect
- [ ] WebChat ConnectedAccountsStore with connect/disconnect UI
- [ ] Dockerfile for McpServerCalendar
- [ ] docker-compose.yml entry for mcp-calendar service
- [ ] Agent appsettings.json updated with mcp-calendar endpoint
- [ ] .env file updated with Microsoft OAuth placeholders
- [ ] Provider abstraction: adding a new calendar provider requires only a new ICalendarProvider implementation
- [ ] MCP server is stateless: receives access token per-call, no auth logic
- [ ] All unit tests pass
- [ ] All integration tests pass
- [ ] No Domain layer violations (no infrastructure imports in Domain)
- [ ] Solution builds with no errors

**You MUST:**
1. Run the full test suite: `dotnet test Tests/Tests.csproj`
2. Build the solution: `dotnet build`
3. Verify the dependency graph (Domain has no Infrastructure references)
4. Write at least 3 additional integration tests targeting cross-feature boundaries
5. Produce a final verdict

**Commit additional tests:** `git commit -m "test: add final adversarial integration tests for calendar feature"`

---

## Dependency Graph

```
Task 0 (scaffolding)
├──── Feature 1 (domain tools) ──────┐
├──── Feature 2 (Redis token store) ──┼──── Feature 4 (MCP tools + wiring)
└──── Feature 3 (Graph provider) ────┘              │
                                                     │
       Feature 2 ──── Feature 5 (Agent OAuth) ──── Feature 6 (WebChat)
                                                     │
                           ┌─────────────────────────┘
                           ▼
                    Feature 7 (integration)
```

**Parallel tracks after Task 0:**
- Track A: Feature 1 → Feature 4
- Track B: Feature 2 → Feature 5 → Feature 6
- Track C: Feature 3

Feature 4 depends on Features 1 and 3 (domain tools + Graph provider for ConfigModule DI).
Feature 7 depends on Features 4, 5, and 6.

## Execution Instructions

**Recommended:** Execute using subagents for fresh context per task.

For each task, dispatch a fresh subagent using the Task tool:
- subagent_type: "general-purpose"
- Provide the FULL task text in the prompt (don't make subagent read this file)
- Include relevant context from earlier tasks (what was built, where files are)

**Execution order:**
- Tasks within a triplet are strictly sequential: N.1 → N.2 → N.3
- Independent triplets MAY run in parallel if they touch different files
- Dependent triplets are sequential: complete triplet N before starting triplet M

**Parallel execution opportunities:**
- After Task 0: Features 1, 2, and 3 can all start simultaneously (different files)
- Feature 4 starts after Features 1 AND 3 complete
- Feature 5 starts after Feature 2 completes
- Feature 6 starts after Feature 5 completes
- Feature 7 starts after Features 4, 5, and 6 all complete

**Never:**
- Skip a test-writing task (N.1) — "I'll write tests with the implementation"
- Skip an adversarial review task (N.3) — "The tests already pass, it's fine"
- Combine tasks within a triplet — each is a separate subagent dispatch
- Proceed to N.2 if N.1 tests don't compile/exist
- Proceed to N.3 if N.2 tests don't pass
- Proceed to next triplet if N.3 verdict is FAIL
