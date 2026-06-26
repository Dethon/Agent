# Agent-wide Operating Timezone Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a single configured timezone (`Europe/Madrid`) the agent's operating frame — the system-prompt date and per-message timestamps render in it, cron schedules fire at its local wall-clock time (DST-correct), and one-shot `runAt` accepts any timezone and converts internally.

**Architecture:** The timezone is set once as the standard `TZ` environment variable on the `agent` and `mcp-scheduling` containers, so `TimeZoneInfo.Local` (= `TimeProvider.System.GetLocalNow()` / `.LocalTimeZone`) becomes Madrid. Code that needs the zone reads it from an injected `TimeProvider` (testable). Cron evaluation moves from NCrontab to Cronos, which has first-class DST-aware `GetNextOccurrence(DateTimeOffset, TimeZoneInfo)`. Stored timestamps stay UTC everywhere; only what the LLM reads changes zone.

**Tech Stack:** .NET 10, Cronos (cron), Microsoft.Extensions.AI, FakeTimeProvider (Microsoft.Extensions.Time.Testing) for tests, xUnit + Shouldly + Moq, Docker Compose.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-06-26-agent-timezone-design.md`.
- **No trailing newline** in any `.cs` file (`.editorconfig` `insert_final_newline = false`).
- **No XML doc comments**; comment only "why". Prefer LINQ over loops. Primary constructors / `record` DTOs where idiomatic.
- **Storage stays UTC**: `ChatMonitor`, the schedule store, and all `*UtcNow` writes are untouched. Only LLM-facing rendering converts to the local zone.
- **TimeZone value appears exactly once** — in the docker-compose YAML anchor. No app config key.
- **Every commit message** ends with a trailer line: `Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg`.
- **Commit after each task** (each task is a green build + passing tests).
- Run a focused test with: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<ClassName>"`.

---

### Task 1: Cronos-backed, timezone-aware `CronValidator`

Replace NCrontab with Cronos and make `GetNextOccurrence` take a `DateTimeOffset` + `TimeZoneInfo`, returning a UTC `DateTime`. Update all three callers so the solution still compiles and behaves as today (callers pass UTC for now; the real zone is wired in Tasks 3–4).

**Files:**
- Modify: `Infrastructure/Infrastructure.csproj` (add Cronos)
- Modify: `Domain/Domain.csproj:21` (remove NCrontab)
- Modify: `Domain/Contracts/ICronValidator.cs`
- Modify: `Infrastructure/Validation/CronValidator.cs`
- Modify: `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs:488` (compile-fix, interim UTC)
- Modify: `McpServerScheduling/Services/ScheduleDispatcherService.cs:50-52` (compile-fix, interim UTC)
- Modify: `Infrastructure/Memory/MemoryDreamingService.cs:33` (final — dreaming stays UTC)
- Test: `Tests/Unit/Infrastructure/CronValidatorTests.cs`

**Interfaces:**
- Produces: `ICronValidator.GetNextOccurrence(string cronExpression, DateTimeOffset from, TimeZoneInfo zone) -> DateTime?` (UTC, `DateTimeKind.Utc`); `ICronValidator.IsValid(string) -> bool` (unchanged signature).

- [ ] **Step 1: Add Cronos, remove NCrontab**

```bash
dotnet add Infrastructure/Infrastructure.csproj package Cronos
```

Then delete the NCrontab line from `Domain/Domain.csproj` (line 21):

```xml
      <PackageReference Include="NCrontab" Version="3.4.0" />
```

Verify nothing else uses NCrontab (expect zero hits):

```bash
grep -rn "NCrontab" --include=*.cs . | grep -v "Cronos"
```

- [ ] **Step 2: Write the failing DST test**

Replace the body of `Tests/Unit/Infrastructure/CronValidatorTests.cs` `GetNextOccurrence`-related tests. Update the existing signature calls and add zone/DST coverage:

```csharp
    [Fact]
    public void GetNextOccurrence_ValidCron_ReturnsNextTimeInUtc()
    {
        var from = new DateTimeOffset(2024, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var next = _validator.GetNextOccurrence("0 9 * * *", from, TimeZoneInfo.Utc);

        next.ShouldNotBeNull();
        next.Value.Kind.ShouldBe(DateTimeKind.Utc);
        next.Value.ShouldBe(new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetNextOccurrence_InvalidCron_ReturnsNull()
    {
        var next = _validator.GetNextOccurrence("invalid", DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        next.ShouldBeNull();
    }

    // 09:00 Madrid is 07:00 UTC in summer (CEST, +02:00) and 08:00 UTC in winter (CET, +01:00):
    // the same cron maps to a different UTC instant across DST, which is the whole point.
    [Theory]
    [InlineData("2026-07-01T00:00:00", "2026-07-01T07:00:00")] // summer (CEST)
    [InlineData("2026-01-10T00:00:00", "2026-01-10T08:00:00")] // winter (CET)
    public void GetNextOccurrence_DailyCron_RespectsZoneDstOffset(string fromUtc, string expectedUtc)
    {
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        var from = new DateTimeOffset(DateTime.Parse(fromUtc), TimeSpan.Zero);

        var next = _validator.GetNextOccurrence("0 9 * * *", from, madrid);

        next.ShouldBe(DateTime.SpecifyKind(DateTime.Parse(expectedUtc), DateTimeKind.Utc));
    }
```

- [ ] **Step 3: Run the test, verify it fails to compile/pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CronValidatorTests"`
Expected: FAIL — `GetNextOccurrence` has the old 2-arg signature (compile error).

- [ ] **Step 4: Update the contract**

`Domain/Contracts/ICronValidator.cs`:

```csharp
namespace Domain.Contracts;

public interface ICronValidator
{
    bool IsValid(string cronExpression);
    DateTime? GetNextOccurrence(string cronExpression, DateTimeOffset from, TimeZoneInfo zone);
}
```

- [ ] **Step 5: Reimplement `CronValidator` with Cronos**

`Infrastructure/Validation/CronValidator.cs`:

```csharp
using Cronos;
using Domain.Contracts;

namespace Infrastructure.Validation;

public class CronValidator : ICronValidator
{
    public bool IsValid(string cronExpression) =>
        CronExpression.TryParse(cronExpression, out _);

    // Cronos evaluates the expression against the zone's wall clock with correct DST handling,
    // then we project to a UTC DateTime so the store/score logic stays UTC-keyed.
    public DateTime? GetNextOccurrence(string cronExpression, DateTimeOffset from, TimeZoneInfo zone) =>
        CronExpression.TryParse(cronExpression, out var expr)
            ? expr.GetNextOccurrence(from, zone)?.UtcDateTime
            : null;
}
```

- [ ] **Step 6: Compile-fix the three callers**

`Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` — `ComputeNextRunAt` (interim UTC; the real zone arrives in Task 3):

```csharp
    private DateTime? ComputeNextRunAt(SpecDto spec) =>
        spec.RunAt ?? (spec.Cron is not null
            ? cronValidator.GetNextOccurrence(spec.Cron, DateTimeOffset.UtcNow, TimeZoneInfo.Utc)
            : null);
```

`McpServerScheduling/Services/ScheduleDispatcherService.cs` — inside `DispatchDueAsync` (interim UTC; real zone in Task 4):

```csharp
            var nextRun = schedule.CronExpression is null
                ? null
                : cronValidator.GetNextOccurrence(schedule.CronExpression, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
```

`Infrastructure/Memory/MemoryDreamingService.cs:33` — final form (dreaming stays UTC):

```csharp
            var next = cronValidator.GetNextOccurrence(options.CronSchedule, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
```

- [ ] **Step 7: Run tests, verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CronValidatorTests"`
Expected: PASS (all four facts/theories).

- [ ] **Step 8: Build the whole solution**

Run: `dotnet build agent.sln`
Expected: build succeeds (all callers compile).

- [ ] **Step 9: Commit**

```bash
git add Infrastructure/Infrastructure.csproj Domain/Domain.csproj Domain/Contracts/ICronValidator.cs Infrastructure/Validation/CronValidator.cs Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs McpServerScheduling/Services/ScheduleDispatcherService.cs Infrastructure/Memory/MemoryDreamingService.cs Tests/Unit/Infrastructure/CronValidatorTests.cs
git commit -m "feat(scheduling): timezone-aware cron via Cronos" -m "Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg"
```

---

### Task 2: System-prompt date names the operating zone

`McpAgent.BuildInstructions` already renders the date from `_timeProvider.GetLocalNow()` (Madrid once `TZ` is set). Extend the line to name the zone and offset so the LLM has an explicit frame for scheduling.

**Files:**
- Modify: `Infrastructure/Agents/McpAgent.cs:285-294` (`BuildInstructions`), `:268-275` (`CreateRunOptions` call)
- Test: `Tests/Unit/Infrastructure/McpAgentInstructionsTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `McpAgent.BuildInstructions(string name, string? description, string? customInstructions, IEnumerable<string> domainPrompts, IEnumerable<string> fileSystemPrompts, IEnumerable<string> clientPrompts, DateTimeOffset now, TimeZoneInfo zone) -> string`.

- [ ] **Step 1: Write the failing test**

Add to `Tests/Unit/Infrastructure/McpAgentInstructionsTests.cs`:

```csharp
    [Fact]
    public void BuildInstructions_NamesZoneAndOffset()
    {
        var madrid = TimeZoneInfo.CreateCustomTimeZone("Europe/Madrid", TimeSpan.FromHours(2), "Madrid", "Madrid");
        var now = new DateTimeOffset(2026, 7, 1, 16, 40, 0, TimeSpan.FromHours(2));

        var result = McpAgent.BuildInstructions(
            name: "TestAgent",
            description: null,
            customInstructions: null,
            domainPrompts: [],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: now,
            zone: madrid);

        result.ShouldStartWith("Today is Wednesday, 2026-07-01. Current local time is 16:40 (Europe/Madrid, UTC+02:00).");
    }
```

- [ ] **Step 2: Update the three existing `BuildInstructions` calls** in the same test file to pass `zone: TimeZoneInfo.Utc` (their assertions stay valid because the new line still begins with `"Today is <day>, <date>."`).

Add `zone: TimeZoneInfo.Utc` as the final argument in `BuildInstructions_IncludesCurrentDateAsFirstLine`, `BuildInstructions_KeepsBasePromptAfterDate`, and `BuildInstructions_PlacesCustomInstructionsLast`.

- [ ] **Step 3: Run the test, verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpAgentInstructionsTests"`
Expected: FAIL — `BuildInstructions` has no `zone` parameter (compile error).

- [ ] **Step 4: Implement**

`Infrastructure/Agents/McpAgent.cs` — `BuildInstructions` signature + date line:

```csharp
    internal static string BuildInstructions(
        string name,
        string? description,
        string? customInstructions,
        IEnumerable<string> domainPrompts,
        IEnumerable<string> fileSystemPrompts,
        IEnumerable<string> clientPrompts,
        DateTimeOffset now,
        TimeZoneInfo zone)
    {
        var datePrompt =
            $"Today is {now.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture)}. " +
            $"Current local time is {now.ToString("HH:mm", CultureInfo.InvariantCulture)} " +
            $"({zone.Id}, UTC{now.ToString("zzz", CultureInfo.InvariantCulture)}).";
```

`CreateRunOptions` — pass the provider's zone:

```csharp
            Instructions = BuildInstructions(
                _name,
                _description,
                _customInstructions,
                _domainPrompts,
                session.FileSystemPrompts,
                session.ClientManager.Prompts,
                _timeProvider.GetLocalNow(),
                _timeProvider.LocalTimeZone),
```

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpAgentInstructionsTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/McpAgent.cs Tests/Unit/Infrastructure/McpAgentInstructionsTests.cs
git commit -m "feat(agent): system-prompt date names the operating timezone" -m "Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg"
```

---

### Task 3: `ScheduleFileSystem` — one-shot accepts any zone; cron + rendering use the local zone

Inject `TimeProvider`. Interpret a bare (zoneless) `runAt` in the local zone instead of rejecting it; keep honoring explicit offsets. Compute cron next-run in the local zone. Render stored UTC times back in the local zone for the LLM.

**Files:**
- Modify: `Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` (ctor, `ValidateSpec`, `CreateAsync`/`EditAsync` normalization, `ComputeNextRunAt`, `RenderSpec`/`RenderStatus`, `CreatedAt`)
- Modify: `McpServerScheduling/Modules/ConfigModule.cs:40-50` (register `TimeProvider.System`)
- Test: `Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemJourneyTests.cs`

**Interfaces:**
- Consumes: `ICronValidator.GetNextOccurrence(string, DateTimeOffset, TimeZoneInfo)` (Task 1).
- Produces: `ScheduleFileSystem(IScheduleStore store, IAgentCatalog agents, ICronValidator cronValidator, TimeProvider timeProvider)`.

- [ ] **Step 1: Update the test `Build` helper to inject a fixed-zone clock**

In `Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemJourneyTests.cs`, add a module-level fixed +02:00 zone and pass a `FakeTimeProvider` into `Build`:

```csharp
    private static readonly TimeZoneInfo TestZone =
        TimeZoneInfo.CreateCustomTimeZone("test-plus2", TimeSpan.FromHours(2), "test-plus2", "test-plus2");

    private static ScheduleFileSystem Build(
        FakeScheduleStore? store = null,
        params AgentCatalogEntry[] agents)
    {
        var catalog = new MutableAgentCatalog();
        var entries = agents.Length == 0
            ? new[] { new AgentCatalogEntry("jonas", "Jonas", "general") }
            : agents;
        catalog.Replace(entries);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TestZone);
        return new ScheduleFileSystem(store ?? new FakeScheduleStore(), catalog, new CronValidator(), clock);
    }
```

Add `using Microsoft.Extensions.Time.Testing;` to the test file if not already present.

- [ ] **Step 2: Write the failing acceptance test for bare `runAt`**

Add a new test (a bare local datetime is interpreted in the local zone, +02:00 here, then stored as UTC):

```csharp
    [Theory]
    [InlineData("2999-01-01T00:00:00", "2998-12-31T22:00:00")]    // bare → local +02:00 → UTC
    [InlineData("2999-01-01T14:30:00.5", "2999-01-01T12:30:00.5")] // bare with fractional seconds
    public async Task Create_BareRunAt_InterpretedInLocalZone(string runAtInput, string expectedUtc)
    {
        var store = new FakeScheduleStore();
        var fs = Build(store);

        var result = await fs.CreateAsync(
            "/jonas/wake/schedule.json",
            $$"""{"prompt":"p","runAt":"{{runAtInput}}"}""",
            false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var saved = store.Items["wake"];
        saved.RunAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        saved.RunAt.ShouldBe(DateTime.SpecifyKind(
            DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Utc));
    }
```

> The test file already imports `System.Globalization` (used by `Create_ZonedRunAt_NormalizesToUtc`). The new theory reuses that import.

- [ ] **Step 3: Flip the existing rejection test**

In `Create_RejectsDuplicateIdConflictingTriggersAndUnzonedRunAt` (around line 303), **remove** the two assertions that expect a zoneless `runAt` to be rejected (the blocks creating `"""{"prompt":"p","runAt":"2999-01-01T00:00:00"}"""` and `"""{"prompt":"p","runAt":"2999-01-01T14:30:00.5"}"""` and asserting an `Err`). Rename the test to `Create_RejectsDuplicateIdAndConflictingTriggers`. Keep the duplicate-id and cron+runAt-conflict rejection assertions.

- [ ] **Step 4: Run the tests, verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleFileSystemJourneyTests"`
Expected: FAIL — ctor has no 4th parameter (compile error) and the bare-runAt create is currently rejected.

- [ ] **Step 5: Inject `TimeProvider` and add zone helpers**

`Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs` — ctor:

```csharp
public sealed class ScheduleFileSystem(
    IScheduleStore store,
    IAgentCatalog agents,
    ICronValidator cronValidator,
    TimeProvider timeProvider) : IFileSystemBackend
```

Add private helpers (place near `ComputeNextRunAt`):

```csharp
    // A bare (zoneless) runAt is wall-clock time in the operating zone; an offset/Z runAt is honored.
    private DateTime ToUtc(DateTime runAt) =>
        runAt.Kind == DateTimeKind.Unspecified
            ? TimeZoneInfo.ConvertTimeToUtc(runAt, timeProvider.LocalTimeZone)
            : runAt.ToUniversalTime();

    // Stored times are UTC; render them in the operating zone so the LLM reads local wall-clock.
    private DateTimeOffset? ToZone(DateTime? utc) =>
        utc is { } u
            ? TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)), timeProvider.LocalTimeZone)
            : null;
```

- [ ] **Step 6: Use the zone in validation, normalization, compute, and rendering**

`ValidateSpec` — replace the `runAt` block (remove the `Unspecified` rejection; future-check via `ToUtc`):

```csharp
        if (spec.RunAt is { } runAt && ToUtc(runAt) <= timeProvider.GetUtcNow().UtcDateTime)
        {
            return Error(ToolError.Codes.InvalidArgument, "runAt must be in the future");
        }
```

`CreateAsync` (line ~245) and `EditAsync` (line ~297) — normalize via `ToUtc`:

```csharp
        spec = spec! with { RunAt = spec.RunAt is { } r ? ToUtc(r) : null };
```

`CreateAsync` `CreatedAt` (line ~256):

```csharp
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
```

`ComputeNextRunAt`:

```csharp
    private DateTime? ComputeNextRunAt(SpecDto spec) =>
        spec.RunAt ?? (spec.Cron is not null
            ? cronValidator.GetNextOccurrence(spec.Cron, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone)
            : null);
```

`RenderSpec`/`RenderStatus` — make them instance methods and convert with `ToZone`:

```csharp
    private string RenderSpec(Schedule s) => JsonSerializer.Serialize(new
    {
        prompt = s.Prompt,
        cron = s.CronExpression,
        runAt = ToZone(s.RunAt),
        userId = s.UserId,
        deliverTo = s.DeliverTo
    }, _json);

    private string RenderStatus(Schedule s) => JsonSerializer.Serialize(new
    {
        createdAt = ToZone(s.CreatedAt),
        lastRunAt = ToZone(s.LastRunAt),
        nextRunAt = ToZone(s.NextRunAt)
    }, _json);
```

- [ ] **Step 7: Register `TimeProvider` in the scheduling DI**

`McpServerScheduling/Modules/ConfigModule.cs` — add to the `services` chain (before `AddSingleton<ScheduleFileSystem>()`):

```csharp
            .AddSingleton(TimeProvider.System)
```

- [ ] **Step 8: Run tests, verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleFileSystemJourneyTests"`
Expected: PASS (new bare-runAt theory passes; explicit-offset theory still passes; renamed rejection test passes).

- [ ] **Step 9: Build the solution**

Run: `dotnet build agent.sln`
Expected: succeeds.

- [ ] **Step 10: Commit**

```bash
git add Domain/Tools/Scheduling/Vfs/ScheduleFileSystem.cs McpServerScheduling/Modules/ConfigModule.cs Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemJourneyTests.cs
git commit -m "feat(scheduling): one-shot accepts any zone; cron/render use operating zone" -m "Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg"
```

---

### Task 4: `ScheduleDispatcherService` evaluates cron in the operating zone

Swap the dispatcher's interim UTC clock/zone for the injected `TimeProvider`'s local zone, so fired recurring schedules recompute their next run at the configured wall-clock time.

**Files:**
- Modify: `McpServerScheduling/Services/ScheduleDispatcherService.cs` (ctor + `DispatchDueAsync`)
- Test: `Tests/Unit/McpServerScheduling/ScheduleDispatcherServiceTests.cs`

**Interfaces:**
- Consumes: `ICronValidator.GetNextOccurrence(string, DateTimeOffset, TimeZoneInfo)`; `TimeProvider` (registered in Task 3).
- Produces: `ScheduleDispatcherService(IScheduleStore, ICronValidator, IScheduleNotificationEmitter, SchedulingSettings, ILogger<ScheduleDispatcherService>, TimeProvider)`.

- [ ] **Step 1: Inspect the existing dispatcher test construction**

Run: `grep -n "new ScheduleDispatcherService(" Tests/Unit/McpServerScheduling/ScheduleDispatcherServiceTests.cs`
Note each call site — every one needs the new `TimeProvider` argument.

- [ ] **Step 2: Write/adjust a failing test that pins zone-based next-run**

Add to `Tests/Unit/McpServerScheduling/ScheduleDispatcherServiceTests.cs` (use the file's existing fakes for store/emitter/settings; if the file already has helpers, reuse them). This test asserts a daily `0 9 * * *` schedule, when fired, gets its `NextRunAt` recomputed to 07:00 UTC under a +02:00 zone:

```csharp
    [Fact]
    public async Task DispatchDueAsync_RecurringSchedule_RecomputesNextRunInLocalZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus2", TimeSpan.FromHours(2), "p2", "p2");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 6, 30, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(zone);

        // Arrange a single due recurring schedule in the store + an emitter with an active session.
        // (Reuse the file's existing fakes; see other tests in this class for the exact setup.)
        var store = new FakeScheduleStore();
        await store.CreateAsync(new Schedule
        {
            Id = "s", AgentId = "jonas", Prompt = "p",
            CronExpression = "0 9 * * *",
            NextRunAt = new DateTime(2026, 7, 1, 5, 0, 0, DateTimeKind.Utc) // already due
        });
        var emitter = new FakeEmitter(hasSessions: true, accept: true);
        var sut = new ScheduleDispatcherService(
            store, new CronValidator(), emitter,
            new SchedulingSettings { RedisConnectionString = "x" },
            NullLogger<ScheduleDispatcherService>.Instance, clock);

        await sut.DispatchDueAsync(CancellationToken.None);

        store.Items["s"].NextRunAt.ShouldBe(new DateTime(2026, 7, 1, 7, 0, 0, DateTimeKind.Utc));
    }
```

> If the test class does not already expose `FakeScheduleStore`/`FakeEmitter` with these shapes, adapt the test to the fakes that exist in the file (the assertion — `NextRunAt == 07:00Z` — is the point). Add `using Microsoft.Extensions.Time.Testing;` and `using Microsoft.Extensions.Logging.Abstractions;` if missing.

- [ ] **Step 3: Run the test, verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleDispatcherServiceTests"`
Expected: FAIL — ctor lacks the `TimeProvider` parameter (compile error).

- [ ] **Step 4: Implement**

`McpServerScheduling/Services/ScheduleDispatcherService.cs` — add `TimeProvider timeProvider` to the primary constructor:

```csharp
public sealed class ScheduleDispatcherService(
    IScheduleStore store,
    ICronValidator cronValidator,
    IScheduleNotificationEmitter emitter,
    SchedulingSettings settings,
    ILogger<ScheduleDispatcherService> logger,
    TimeProvider timeProvider) : BackgroundService
```

In `DispatchDueAsync`, use the provider's clock and zone:

```csharp
        var now = timeProvider.GetUtcNow();
        var due = await store.GetDueSchedulesAsync(now.UtcDateTime, ct);
        foreach (var schedule in due)
        {
            var nextRun = schedule.CronExpression is null
                ? null
                : cronValidator.GetNextOccurrence(schedule.CronExpression, now, timeProvider.LocalTimeZone);
```

And the `UpdateLastRunAsync` call in the same loop:

```csharp
                await store.UpdateLastRunAsync(schedule.Id, now.UtcDateTime, plan.NextRunAt, ct);
```

- [ ] **Step 5: Update any other `new ScheduleDispatcherService(...)` call sites** found in Step 1 to pass `TimeProvider.System` (or the test's fake clock).

- [ ] **Step 6: Run tests, verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ScheduleDispatcherServiceTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add McpServerScheduling/Services/ScheduleDispatcherService.cs Tests/Unit/McpServerScheduling/ScheduleDispatcherServiceTests.cs
git commit -m "feat(scheduling): dispatcher recomputes cron in the operating zone" -m "Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg"
```

---

### Task 5: `SchedulingPrompt.Build(zoneId)` teaches the zone

Turn the `Prompt` constant into a `Build(zoneId)` method so the LLM is told cron runs in the operating zone and `runAt` accepts any zone (or a bare local time interpreted as the operating zone).

**Files:**
- Modify: `Domain/Prompts/SchedulingPrompt.cs`
- Modify: `McpServerScheduling/McpPrompts/McpSystemPrompt.cs`
- Test: `Tests/Unit/Domain/Prompts/VfsPromptToolNameConsistencyTests.cs`

**Interfaces:**
- Produces: `SchedulingPrompt.Build(string zoneId) -> string`; `SchedulingPrompt.Name`, `SchedulingPrompt.Description` unchanged.

- [ ] **Step 1: Update the consistency test to the new API**

In `Tests/Unit/Domain/Prompts/VfsPromptToolNameConsistencyTests.cs`:
- Line 17: change `["scheduling_prompt", SchedulingPrompt.Prompt]` → `["scheduling_prompt", SchedulingPrompt.Build("Europe/Madrid")]`.
- Line 38: change `var prompt = SchedulingPrompt.Prompt;` → `var prompt = SchedulingPrompt.Build("Europe/Madrid");`.

Add one assertion to `SchedulingPrompt_ReferencesActualExposedToolLeafNames` that the zone is named:

```csharp
        prompt.ShouldContain("Europe/Madrid");
```

- [ ] **Step 2: Run the test, verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsPromptToolNameConsistencyTests"`
Expected: FAIL — `SchedulingPrompt.Build` / `Prompt` mismatch (compile error).

- [ ] **Step 3: Convert the prompt to a builder**

`Domain/Prompts/SchedulingPrompt.cs` — replace the `Prompt` const with a `Build` method (keep `Name`/`Description`). The cron and `runAt` paragraphs change; tool-leaf names (`text_create`, `glob`, `text_edit`, `move`, `remove`, `exec`) must remain:

```csharp
namespace Domain.Prompts;

public static class SchedulingPrompt
{
    public const string Name = "scheduling_prompt";

    public const string Description =
        "Explains how to schedule agent tasks via the /schedules filesystem (cron/one-shot, delivery, run-now)";

    public static string Build(string zoneId) =>
        $$"""
        ## Scheduled Tasks

        You can schedule prompts to run later — once at a future time, or repeatedly on a cron schedule. Schedules live in the virtual filesystem mounted at `/schedules`, one directory per agent, and you manage them entirely with the `domain__filesystem__*` tools. When a schedule fires, its prompt is delivered to an agent as if a user had sent it.

        ### Layout

        - `/schedules` — the root. Each immediate child directory is an **agent** you can schedule work for.
        - `/schedules/<agentId>/agent_info.json` — read this to learn what an agent does before scheduling against it.
        - `/schedules/<agentId>/<scheduleId>/schedule.json` — one schedule. `<scheduleId>` is a descriptive, unique id you choose (e.g. `morning-news`).
        - `/schedules/<agentId>/<scheduleId>/status.json` — read-only timing: `createdAt`, `lastRunAt`, `nextRunAt`, shown in the **{{zoneId}}** time zone.

        ### Creating a schedule

        `text_create` a `schedule.json` whose content is a JSON object:

        - `prompt` (required) — the instruction delivered to the agent when the schedule fires.
        - `cron` **or** `runAt` — exactly one is required, and they are mutually exclusive.
          - `cron` — a standard 5-field cron expression for a **recurring** schedule. Times are interpreted in the **{{zoneId}}** time zone and adjust automatically across daylight-saving changes. Examples:
            - `"0 9 * * *"` — every day at 09:00 {{zoneId}} time
            - `"0 */2 * * *"` — every 2 hours
            - `"30 14 * * 1-5"` — weekdays at 14:30 {{zoneId}} time
          - `runAt` — an ISO-8601 datetime for a **one-shot** schedule. You may include a time zone — `Z` for UTC (e.g. `2026-06-01T14:30:00Z`) or an explicit offset (e.g. `2026-06-01T16:30:00+02:00`) — or omit it, in which case it is read as **{{zoneId}}** local time (e.g. `2026-06-01T18:00:00`). It is stored as UTC and deleted automatically once it fires.
        - `userId` (optional) — the user the fired prompt should be attributed to.
        - `deliverTo` (optional) — a list of channel ids that should receive the result (e.g. `["signalr", "telegram"]`). Omit to use the configured default.

          **Voice delivery (speak the result aloud).** A `deliverTo` entry may target the voice channel:
          - `"voice"` or `"voice:all"` — speak on every voice satellite.
          - `"voice:<satelliteId>"` — speak on one specific satellite (e.g. `"voice:office-01"`).
          - Repeat `"voice:<satelliteId>"` for several specific satellites — each is spoken once, e.g. `["signalr", "voice:office-01", "voice:kitchen-01"]`.

          Add a voice target **only when the user explicitly asked to be notified by voice** (spoken aloud / announced). Otherwise omit voice — **silence is the default**. For example, a schedule that starts the air conditioning at night must NOT announce. Offline satellites are skipped silently. To keep tool-approval prompts answerable, list a non-voice channel first, e.g. `["signalr", "voice:fran-office-01"]`.

        A recurring schedule — every day at 09:00 {{zoneId}} time:

        ```json
        {
          "prompt": "Summarize today's tech news and send me the highlights",
          "cron": "0 9 * * *",
          "deliverTo": ["signalr"]
        }
        ```

        A one-shot schedule — fires once, then deletes itself:

        ```json
        {
          "prompt": "Remind me to submit the quarterly report",
          "runAt": "2026-06-01T14:30:00"
        }
        ```

        ### Managing schedules

        - **Discover** — `glob` `/schedules` to list agents, then glob `/schedules/<agentId>` to list their schedules.
        - **Change** — `text_edit` the `schedule.json` to adjust the prompt, timing, or delivery.
        - **Reassign / rename** — `move` a schedule directory to a different `<agentId>` or `<scheduleId>`.
        - **Remove** — `remove` the schedule directory.
        - **Run now** — `exec` `run_now.sh` on a schedule directory to fire it immediately without waiting for its next scheduled time.
        """;
}
```

- [ ] **Step 4: Update the prompt consumer**

`McpServerScheduling/McpPrompts/McpSystemPrompt.cs` — call `Build` with the running container's local zone:

```csharp
    [McpServerPrompt(Name = SchedulingPrompt.Name)]
    [Description(SchedulingPrompt.Description)]
    public string GetSchedulingPrompt()
    {
        var setup = summary.Get();
        var prompt = SchedulingPrompt.Build(TimeZoneInfo.Local.Id);
        return string.IsNullOrEmpty(setup)
            ? prompt
            : prompt + "\n\n" + setup;
    }
```

- [ ] **Step 5: Run tests, verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsPromptToolNameConsistencyTests"`
Expected: PASS (no `fs_`-prefixed names; all leaf names present; `Europe/Madrid` named).

- [ ] **Step 6: Commit**

```bash
git add Domain/Prompts/SchedulingPrompt.cs McpServerScheduling/McpPrompts/McpSystemPrompt.cs Tests/Unit/Domain/Prompts/VfsPromptToolNameConsistencyTests.cs
git commit -m "feat(scheduling): prompt teaches the operating timezone for cron and runAt" -m "Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg"
```

---

### Task 6: Per-message timestamp renders in the operating zone

`OpenRouterChatClient` gets an injected `TimeProvider`; the `[Current time: …]` prefix converts the stored UTC timestamp to the local zone before formatting, so the offset reflects the operating zone.

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` (both ctors, prefix render, line 145 stamp)
- Test: `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientPrefixTests.cs`

**Interfaces:**
- Produces: `OpenRouterChatClient(IChatClient innerClient, string model, int? maxContextTokens = null, IMetricsPublisher? metricsPublisher = null, TimeProvider? timeProvider = null)` (internal); public ctor gains a trailing `TimeProvider? timeProvider = null`.

- [ ] **Step 1: Write the failing test**

Add to `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientPrefixTests.cs`:

```csharp
    [Fact]
    public async Task GetStreamingResponseAsync_RendersTimestampInLocalZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus2", TimeSpan.FromHours(2), "p2", "p2");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        clock.SetLocalTimeZone(zone);
        using var sut = new OpenRouterChatClient(_innerClient.Object, "test-model", timeProvider: clock);

        var msg = new ChatMessage(ChatRole.User, "hi");
        msg.SetSenderId("u");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero)); // 18:22:01 UTC

        await sut.GetStreamingResponseAsync([msg]).ToListAsync();

        var first = _captured[0].Contents.OfType<TextContent>().First().Text;
        first.ShouldStartWith("[Current time: 2026-06-04 20:22:01 +02:00]");
    }
```

Add `using Microsoft.Extensions.Time.Testing;` to the test file if missing.

- [ ] **Step 2: Run the test, verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClientPrefixTests"`
Expected: FAIL — the prefix currently shows `+00:00` (and the `timeProvider:` argument doesn't exist yet → compile error).

- [ ] **Step 3: Implement**

`Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`:

Add a field:

```csharp
    private readonly TimeProvider _timeProvider;
```

Public ctor — add the trailing parameter and assignment:

```csharp
    public OpenRouterChatClient(
        string endpoint,
        string apiKey,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        string? sessionId = null,
        TimeProvider? timeProvider = null)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _metricsPublisher = metricsPublisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpClient = CreateHttpClient(_reasoningQueue, _costQueue, sessionId);
        _transport = new HttpClientPipelineTransport(_httpClient);
        _client = CreateClient(endpoint, apiKey, model, _transport);
    }
```

Internal ctor — add the trailing parameter and assignment:

```csharp
    internal OpenRouterChatClient(
        IChatClient innerClient,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        TimeProvider? timeProvider = null)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _metricsPublisher = metricsPublisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _client = innerClient;
    }
```

In `GetStreamingResponseAsync`, compute the local timestamp before the prefix switch and use it in the two `[Current time: …]` arms:

```csharp
                var localTimestamp = timestamp is { } ts
                    ? TimeZoneInfo.ConvertTime(ts, _timeProvider.LocalTimeZone)
                    : (DateTimeOffset?)null;

                var prefix = (senderSegment, timestamp) switch
                {
                    (not null, not null) => $"[Current time: {localTimestamp:yyyy-MM-dd HH:mm:ss zzz}] {senderSegment}:\n",
                    (not null, null) => $"{senderSegment}:\n",
                    (null, not null) => $"[Current time: {localTimestamp:yyyy-MM-dd HH:mm:ss zzz}]:\n",
                    _ => ""
                };
```

Replace the stamp at line ~145:

```csharp
            update.SetTimestamp(_timeProvider.GetUtcNow());
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClientPrefixTests"`
Expected: PASS (new local-zone test passes; existing `ShouldStartWith("[Current time: ")` tests still pass).

- [ ] **Step 5: Build the solution**

Run: `dotnet build agent.sln`
Expected: succeeds (existing call sites in `MultiAgentFactory`/`MemoryModule` keep working — the new parameter is optional).

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientPrefixTests.cs
git commit -m "feat(agent): per-message timestamp renders in the operating timezone" -m "Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg"
```

---

### Task 7: Set `TZ=Europe/Madrid` once in docker-compose + verify tzdata

Define the timezone in a single YAML anchor and apply it to the two containers that need it. Verify `Europe/Madrid` resolves inside the runtime image.

**Files:**
- Modify: `DockerCompose/docker-compose.yml`
- Possibly modify: the runtime Dockerfile(s) if `tzdata` is missing.

- [ ] **Step 1: Add the anchor and apply it to both services**

`DockerCompose/docker-compose.yml` — add the anchor under the top `name:` (before `services:`):

```yaml
name: jackbot

# Single source of truth for the agent's operating timezone (applied to the two
# containers that surface time to the LLM / fire schedules).
x-timezone: &timezone
  TZ: Europe/Madrid

services:
```

In the `agent:` service (after its `env_file:` block), add:

```yaml
    environment:
      <<: *timezone
```

In the `mcp-scheduling:` service (after its `env_file:` block), add:

```yaml
    environment:
      <<: *timezone
```

- [ ] **Step 2: Validate the compose file parses**

Run: `docker compose -f DockerCompose/docker-compose.yml config >/dev/null && echo OK`
Expected: `OK` (and `docker compose ... config | grep -A1 "TZ"` shows `TZ: Europe/Madrid` resolved on both services).

- [ ] **Step 3: Verify the zone resolves in the agent runtime image**

Build/start the `agent` container per the project's launch command, then:

Run:
```bash
docker compose -p jackbot exec agent sh -lc 'date +%Z%z; ls /usr/share/zoneinfo/Europe/Madrid'
```
Expected: prints a Madrid abbreviation/offset (e.g. `CEST+0200`) and the zoneinfo path exists.

If the file is **missing** (slim base without tzdata), add tzdata to the runtime stage of `Agent/Dockerfile` and `McpServerScheduling/Dockerfile`:

```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*
```

Rebuild and re-run the verification.

- [ ] **Step 4: Smoke-check end to end**

With both containers up, send the agent a message and confirm the user-turn prefix shows the Madrid offset (`[Current time: … +02:00]` in summer), and create a one-shot `runAt` without a zone (e.g. a couple of minutes ahead in local time) — confirm `status.json` shows `nextRunAt` in Madrid time and it fires at the right wall-clock moment.

- [ ] **Step 5: Commit**

```bash
git add DockerCompose/docker-compose.yml
# include Dockerfile(s) only if tzdata had to be added:
# git add Agent/Dockerfile McpServerScheduling/Dockerfile
git commit -m "feat(deploy): set TZ=Europe/Madrid for agent and scheduler containers" -m "Claude-Session: https://claude.ai/code/session_01TBmeQeS3xHmM4tjUi8iZkg"
```

---

## Final Verification

- [ ] Run the full unit suite: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Unit"` — all green.
- [ ] `dotnet build agent.sln` — clean.
- [ ] `grep -rn "NCrontab" --include=*.cs --include=*.csproj .` — zero hits (Cronos fully replaced it).
- [ ] Manual smoke check from Task 7 Step 4 confirms Madrid timestamps + a zoneless `runAt` firing at the correct local time.
