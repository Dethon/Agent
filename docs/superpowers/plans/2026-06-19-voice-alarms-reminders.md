# Voice Alarms & Reminders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver alarms/reminders as insistent spoken messages on voice satellites — triggered by Home Assistant calendar events hitting the existing announce endpoint in a new "insistent" mode, repeating until the user acknowledges by waking a satellite.

**Architecture:** Home Assistant owns timing (a local calendar event + one bridging automation `rest_command` → `POST /api/voice/announce` with an `insistent` block). The voice hub owns delivery: a new `InsistentAnnouncementController` plays the message (High priority) on every targeted online satellite and repeats on a gap until a safety cap. Acknowledgment rides the satellite's existing local-wake → transcribe → dispatch path: when a real utterance is dispatched from a satellite with an active alert, `WyomingSatelliteHost` calls `ActiveAlertRegistry.Acknowledge`, which cancels the alert on all targeted satellites. The in-house scheduler is **not** touched.

**Tech Stack:** .NET 10, ASP.NET minimal API (announce endpoint), MCP, Wyoming protocol, Redis metrics, xUnit + Shouldly + Moq, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-06-19-voice-alarms-reminders-design.md` (read §4 — the satellite-constraint / wake-word-ack section — before Task 4/5).

## Global Constraints

- **No trailing newline in any `.cs` file** (`.editorconfig` `insert_final_newline = false`). Applies to test files too.
- File-scoped namespaces; `record` types for DTOs; primary constructors for DI.
- `ArgumentNullException.ThrowIfNull()` for guard clauses; `TimeProvider` for time-dependent code (never `DateTime.Now`/`Task.Delay` without the provider in production paths).
- Prefer LINQ over loops, except for unavoidable side-effecting mutation.
- No XML-doc comments; comment only "why".
- Tests: `Tests/Unit/` for isolated logic, `Tests/Integration/` for socket/host tests. Class names `{ClassUnderTest}Tests`; methods `{Method}_{Scenario}_{ExpectedResult}`. Assert with Shouldly.
- TDD: write the failing test, **run it and capture the RED failure output**, then implement, then GREEN. (Project rule `.claude/rules/testing.md` + memory: implementers must show the RED failure before GREEN.)
- The pre-commit hook runs `dotnet format` over staged `.cs` files and re-stages them **whole** — make the working tree match each commit.
- Commit messages end with: `Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt`
- Build/test commands:
  - Build: `dotnet build agent.sln`
  - Unit test a class: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<ClassName>"`

---

### Task 1: Insistent request contract, settings defaults, and plan resolution

**Files:**
- Create: `Domain/DTOs/Voice/InsistentOptions.cs`
- Modify: `Domain/DTOs/Voice/AnnounceRequest.cs`
- Modify: `McpChannelVoice/Settings/AnnounceSettings.cs`
- Create: `McpChannelVoice/Services/InsistentPlan.cs`
- Test: `Tests/Unit/McpChannelVoice/InsistentPlanTests.cs`

**Interfaces:**
- Produces:
  - `Domain.DTOs.Voice.InsistentOptions` — `record { int? GapSeconds; int? MaxRepeats; int? MaxDurationSeconds }`.
  - `AnnounceRequest.Insistent` — `InsistentOptions?` (null ⇒ existing one-shot announce, unchanged).
  - `McpChannelVoice.Settings.InsistentDefaults` — `record { int GapSeconds = 30; int MaxRepeats = 5; int? MaxDurationSeconds = null }`, exposed as `AnnounceSettings.Insistent`.
  - `McpChannelVoice.Services.InsistentPlan` — `readonly record struct (TimeSpan Gap, int MaxRepeats, TimeSpan? MaxDuration)` with `static InsistentPlan Resolve(InsistentOptions?, InsistentDefaults)`.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/InsistentPlanTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class InsistentPlanTests
{
    private static readonly InsistentDefaults Defaults = new();

    [Fact]
    public void Resolve_NullOptions_UsesDefaults()
    {
        var plan = InsistentPlan.Resolve(null, Defaults);

        plan.Gap.ShouldBe(TimeSpan.FromSeconds(30));
        plan.MaxRepeats.ShouldBe(5);
        plan.MaxDuration.ShouldBeNull();
    }

    [Fact]
    public void Resolve_RequestOverridesGapAndRepeats()
    {
        var plan = InsistentPlan.Resolve(new InsistentOptions { GapSeconds = 10, MaxRepeats = 3 }, Defaults);

        plan.Gap.ShouldBe(TimeSpan.FromSeconds(10));
        plan.MaxRepeats.ShouldBe(3);
    }

    [Fact]
    public void Resolve_DurationOnly_IsDurationBoundedNotClippedToDefaultRepeats()
    {
        // A request that sets only MaxDurationSeconds must be bounded by duration, not also
        // silently capped at the default repeat count.
        var plan = InsistentPlan.Resolve(new InsistentOptions { MaxDurationSeconds = 120 }, Defaults);

        plan.MaxRepeats.ShouldBe(int.MaxValue);
        plan.MaxDuration.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Resolve_NonPositiveDuration_IsTreatedAsNoDurationCap()
    {
        var plan = InsistentPlan.Resolve(new InsistentOptions { MaxDurationSeconds = 0 }, Defaults);

        plan.MaxDuration.ShouldBeNull();
        plan.MaxRepeats.ShouldBe(5);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentPlanTests"`
Expected: FAIL — `InsistentOptions`, `InsistentPlan`, `InsistentDefaults` do not exist (compile error).

- [ ] **Step 3: Create the DTO**

Create `Domain/DTOs/Voice/InsistentOptions.cs`:

```csharp
namespace Domain.DTOs.Voice;

public record InsistentOptions
{
    public int? GapSeconds { get; init; }
    public int? MaxRepeats { get; init; }
    public int? MaxDurationSeconds { get; init; }
}
```

- [ ] **Step 4: Add `Insistent` to `AnnounceRequest`**

Modify `Domain/DTOs/Voice/AnnounceRequest.cs` — add the property after `Priority`:

```csharp
namespace Domain.DTOs.Voice;

public record AnnounceRequest
{
    public required AnnounceTarget Target { get; init; }
    public required string Text { get; init; }
    public string? Voice { get; init; }
    public AnnouncePriority Priority { get; init; } = AnnouncePriority.Normal;
    public InsistentOptions? Insistent { get; init; }
}
```

- [ ] **Step 5: Add `InsistentDefaults` to settings**

Modify `McpChannelVoice/Settings/AnnounceSettings.cs`:

```csharp
namespace McpChannelVoice.Settings;

public record AnnounceSettings
{
    public bool Enabled { get; init; } = true;
    public string Token { get; init; } = "";
    public bool BindToLoopbackOnly { get; init; }
    public int QueueMaxDepth { get; init; } = 8;
    public int MaxTextLength { get; init; } = 50000;
    public InsistentDefaults Insistent { get; init; } = new();
}

public record InsistentDefaults
{
    public int GapSeconds { get; init; } = 30;
    public int MaxRepeats { get; init; } = 5;
    public int? MaxDurationSeconds { get; init; }
}
```

- [ ] **Step 6: Create `InsistentPlan`**

Create `McpChannelVoice/Services/InsistentPlan.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public readonly record struct InsistentPlan(TimeSpan Gap, int MaxRepeats, TimeSpan? MaxDuration)
{
    public static InsistentPlan Resolve(InsistentOptions? options, InsistentDefaults defaults)
    {
        var gap = TimeSpan.FromSeconds(options?.GapSeconds ?? defaults.GapSeconds);

        // A request that sets only a duration is duration-bounded (unbounded repeats); otherwise the
        // repeat count applies. With both set, the loop stops at whichever is reached first.
        var maxRepeats = options?.MaxRepeats
            ?? (options?.MaxDurationSeconds is > 0 ? int.MaxValue : defaults.MaxRepeats);

        var maxDurationSeconds = options?.MaxDurationSeconds ?? defaults.MaxDurationSeconds;
        var maxDuration = maxDurationSeconds is > 0
            ? TimeSpan.FromSeconds(maxDurationSeconds.Value)
            : (TimeSpan?)null;

        return new InsistentPlan(gap, maxRepeats, maxDuration);
    }
}
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentPlanTests"`
Expected: PASS (4 tests).

- [ ] **Step 8: Commit**

```bash
git add Domain/DTOs/Voice/InsistentOptions.cs Domain/DTOs/Voice/AnnounceRequest.cs \
        McpChannelVoice/Settings/AnnounceSettings.cs McpChannelVoice/Services/InsistentPlan.cs \
        Tests/Unit/McpChannelVoice/InsistentPlanTests.cs
git commit -m "feat(voice): insistent announce contract, defaults, and plan resolution

Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt"
```

---

### Task 2: Alarm metric enum values

**Files:**
- Modify: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`
- Modify: `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs`

**Interfaces:**
- Produces: `VoiceMetric.AlarmAcknowledged = 15`, `VoiceMetric.AlarmUnacknowledged = 16`, `VoiceMetric.AlarmOffline = 17`.

Values are persisted as integers in Redis — **append only, never renumber** (see the enum's own comment).

- [ ] **Step 1: Write the failing test**

Add three `[InlineData]` rows to the existing `VoiceMetric_HasPinnedWireValues` theory in `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs`, right after the `FollowUpTimedOut, 14` line:

```csharp
    [InlineData(VoiceMetric.FollowUpTimedOut, 14)]
    [InlineData(VoiceMetric.AlarmAcknowledged, 15)]
    [InlineData(VoiceMetric.AlarmUnacknowledged, 16)]
    [InlineData(VoiceMetric.AlarmOffline, 17)]
    public void VoiceMetric_HasPinnedWireValues(VoiceMetric metric, int expected) =>
        ((int)metric).ShouldBe(expected);
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: FAIL — `VoiceMetric.AlarmAcknowledged` etc. do not exist (compile error).

- [ ] **Step 3: Append the enum members**

Modify `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` — append after `FollowUpTimedOut = 14`:

```csharp
    FollowUpTimedOut = 14,
    AlarmAcknowledged = 15,
    AlarmUnacknowledged = 16,
    AlarmOffline = 17
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/VoiceMetric.cs Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs
git commit -m "feat(voice): add pinned Alarm* voice metric values

Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt"
```

---

### Task 3: ActiveAlertRegistry — multi-satellite acknowledgment routing

**Files:**
- Create: `McpChannelVoice/Services/ActiveAlertRegistry.cs`
- Test: `Tests/Unit/McpChannelVoice/ActiveAlertRegistryTests.cs`

**Interfaces:**
- Produces:
  - `AlertHandle` — `sealed class` wrapping a `CancellationTokenSource` and the alert's targeted satellite ids; `Token` (CancellationToken), `IsAcknowledged` (bool), `SatelliteIds` (IReadOnlyList<string>), `void Acknowledge()` (sets flag + cancels).
  - `ActiveAlertRegistry` — `void Register(AlertHandle)`, `bool Acknowledge(string satelliteId)` (cancels + removes the alert for any one of its satellites; returns false if none active), `void Discard(AlertHandle)` (loop cleanup).
- Consumed by: Task 4 (controller registers/discards) and Task 5 (host calls `Acknowledge`).

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/ActiveAlertRegistryTests.cs`:

```csharp
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ActiveAlertRegistryTests
{
    [Fact]
    public void Acknowledge_OnAnyTargetedSatellite_CancelsTheSharedAlert()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        var handle = new AlertHandle(cts, ["kitchen-01", "bedroom-01"]);
        registry.Register(handle);

        var acknowledged = registry.Acknowledge("bedroom-01");

        acknowledged.ShouldBeTrue();
        handle.IsAcknowledged.ShouldBeTrue();
        handle.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Acknowledge_RemovesEveryTargetEntry_SoASecondAckIsANoOp()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register(new AlertHandle(cts, ["kitchen-01", "bedroom-01"]));

        registry.Acknowledge("kitchen-01").ShouldBeTrue();
        registry.Acknowledge("bedroom-01").ShouldBeFalse(); // already cleared by the first ack
    }

    [Fact]
    public void Acknowledge_UnknownSatellite_ReturnsFalse()
    {
        var registry = new ActiveAlertRegistry();

        registry.Acknowledge("ghost").ShouldBeFalse();
    }

    [Fact]
    public void Discard_RemovesEntries_WithoutAcknowledging()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        var handle = new AlertHandle(cts, ["kitchen-01"]);
        registry.Register(handle);

        registry.Discard(handle);

        registry.Acknowledge("kitchen-01").ShouldBeFalse();
        handle.IsAcknowledged.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ActiveAlertRegistryTests"`
Expected: FAIL — `ActiveAlertRegistry` / `AlertHandle` do not exist (compile error).

- [ ] **Step 3: Implement the registry**

Create `McpChannelVoice/Services/ActiveAlertRegistry.cs`:

```csharp
using System.Collections.Concurrent;

namespace McpChannelVoice.Services;

public sealed class AlertHandle
{
    private readonly CancellationTokenSource _cts;

    public AlertHandle(CancellationTokenSource cts, IReadOnlyList<string> satelliteIds)
    {
        ArgumentNullException.ThrowIfNull(cts);
        ArgumentNullException.ThrowIfNull(satelliteIds);
        _cts = cts;
        SatelliteIds = satelliteIds;
    }

    public IReadOnlyList<string> SatelliteIds { get; }
    public CancellationToken Token => _cts.Token;
    public bool IsAcknowledged { get; private set; }

    public void Acknowledge()
    {
        IsAcknowledged = true;
        _cts.Cancel();
    }
}

// Maps each targeted satellite id to the alert covering it. The first acknowledgment on ANY of an
// alert's satellites cancels the whole alert (shared CTS) and removes every entry for it, so a later
// wake on a sibling satellite is a no-op.
public sealed class ActiveAlertRegistry
{
    private readonly ConcurrentDictionary<string, AlertHandle> _bySatellite = new();

    public void Register(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        foreach (var id in handle.SatelliteIds)
        {
            _bySatellite[id] = handle;
        }
    }

    public bool Acknowledge(string satelliteId)
    {
        if (!_bySatellite.TryGetValue(satelliteId, out var handle))
        {
            return false;
        }
        handle.Acknowledge();
        Discard(handle);
        return true;
    }

    public void Discard(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        foreach (var id in handle.SatelliteIds)
        {
            // Remove only if this id still points at THIS handle — a newer alert may already own it.
            _bySatellite.TryRemove(new KeyValuePair<string, AlertHandle>(id, handle));
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ActiveAlertRegistryTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/ActiveAlertRegistry.cs Tests/Unit/McpChannelVoice/ActiveAlertRegistryTests.cs
git commit -m "feat(voice): ActiveAlertRegistry for multi-satellite alarm acknowledgment

Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt"
```

---

### Task 4: InsistentAnnouncementController — repeat-until-acknowledged loop

**Files:**
- Create: `McpChannelVoice/Services/InsistentAnnouncementController.cs`
- Test: `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`

**Interfaces:**
- Consumes: `SatelliteRegistry`, `SatelliteSessionRegistry`, `ITextToSpeech`, `VoiceSettings`, `ActiveAlertRegistry`, `IMetricsPublisher`, `TimeProvider`, `InsistentPlan.Resolve`, `AnnounceTargetNotFoundException` (already declared in `AnnouncementService.cs`).
- Produces: `InsistentAnnouncementController` with `Task<AnnounceResponse> StartAsync(AnnounceRequest, CancellationToken)`. `StartAsync` validates + resolves targets, registers an `AlertHandle`, launches the repeat loop detached, and returns immediately. Acknowledgment is delivered externally via `ActiveAlertRegistry.Acknowledge` (Task 5 wires the host to call it).

Behavior:
- Unknown target (no configured satellites match) → throw `AnnounceTargetNotFoundException` (mirrors `AnnouncementService`).
- No targeted satellite currently online → publish `AlarmOffline` per offline target; response statuses `"offline"`; no loop.
- Synthesize the TTS **once** and replay the buffered chunks each round/satellite.
- Each round: enqueue a High-priority `PlaybackJob` of the buffered audio on every online targeted satellite (re-resolved each round), publish `AnnouncePlayed`, then `await Task.Delay(plan.Gap, timeProvider, handle.Token)`.
- Stop when `handle.Token` is cancelled (ack), `MaxRepeats` reached, or `MaxDuration` elapsed.
- On ack: `PreemptCurrent()` on every targeted online satellite (stop ringing immediately) + publish `AlarmAcknowledged`.
- On cap with no ack: publish `AlarmUnacknowledged`.
- Always `ActiveAlertRegistry.Discard(handle)` in `finally`.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class InsistentAnnouncementControllerTests
{
    private sealed class CollectingPublisher : IMetricsPublisher
    {
        private readonly List<VoiceEvent> _events = [];
        public IReadOnlyList<VoiceEvent> Events
        {
            get { lock (_events) { return _events.ToList(); } }
        }
        public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            if (metricEvent is VoiceEvent v)
            {
                lock (_events) { _events.Add(v); }
            }
            return Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunk()
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.CompletedTask;
    }

    private sealed record Harness(
        InsistentAnnouncementController Controller,
        SatelliteSessionRegistry Sessions,
        ActiveAlertRegistry Alerts,
        CollectingPublisher Publisher,
        Mock<ITextToSpeech> Tts,
        FakeTimeProvider Time);

    private static Harness BuildHarness(FakeTimeProvider time, bool online, params string[] satelliteIds)
    {
        var configs = satelliteIds.ToDictionary(
            id => id, id => new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        var registry = new SatelliteRegistry(configs);
        var sessions = new SatelliteSessionRegistry();
        if (online)
        {
            foreach (var id in satelliteIds)
            {
                sessions.Register(new SatelliteSession(id, configs[id]));
            }
        }

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => OneChunk());

        var alerts = new ActiveAlertRegistry();
        var publisher = new CollectingPublisher();
        var controller = new InsistentAnnouncementController(
            registry, sessions, tts.Object, new VoiceSettings(), alerts, publisher, time,
            NullLogger<InsistentAnnouncementController>.Instance);
        return new Harness(controller, sessions, alerts, publisher, tts, time);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) { throw new TimeoutException("condition not met"); }
            await Task.Delay(20);
        }
    }

    // Runs each online session's playback loop so enqueued jobs actually play; the writer counts
    // one invocation per round (the mock TTS yields exactly one chunk).
    private static (Task Pump, Func<int> Count) PumpPlays(SatelliteSession session, FakeTimeProvider time)
    {
        var count = 0;
        var pump = session.RunPlaybackLoopAsync(
            (_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; },
            CancellationToken.None, time);
        return (pump, () => Volatile.Read(ref count));
    }

    [Fact]
    public async Task Start_UnknownTarget_Throws()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, "kitchen-01");

        await Should.ThrowAsync<AnnounceTargetNotFoundException>(() =>
            h.Controller.StartAsync(
                new AnnounceRequest { Target = new() { SatelliteId = "ghost" }, Text = "alarm", Insistent = new() },
                CancellationToken.None));
    }

    [Fact]
    public async Task Start_NoOnlineSession_PublishesAlarmOffline()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: false, "kitchen-01"); // configured but not connected

        var response = await h.Controller.StartAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "alarm", Insistent = new() },
            CancellationToken.None);

        response.Satellites.ShouldHaveSingleItem();
        response.Satellites[0].Status.ShouldBe("offline");
        h.Publisher.Events.ShouldContain(e => e.Metric == VoiceMetric.AlarmOffline);
    }

    [Fact]
    public async Task Start_NoAck_RepeatsToCapThenUnacknowledged_SynthesizesOnce()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, "kitchen-01");
        var (pump, plays) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 3 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => plays() >= 1, TimeSpan.FromSeconds(5)); // round 1
        time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => plays() >= 2, TimeSpan.FromSeconds(5)); // round 2
        time.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => plays() >= 3, TimeSpan.FromSeconds(5)); // round 3 (== cap)

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmUnacknowledged),
            TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        plays().ShouldBe(3); // no 4th round after the cap

        h.Tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once); // synthesized once, replayed across rounds

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }

    [Fact]
    public async Task Acknowledge_StopsLoopAndMarksAcknowledged()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, "kitchen-01");
        var (pump, plays) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 10 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => plays() >= 1, TimeSpan.FromSeconds(5));

        h.Alerts.Acknowledge("kitchen-01").ShouldBeTrue();

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmAcknowledged),
            TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromSeconds(120));
        await Task.Delay(50);
        plays().ShouldBe(1); // acknowledged before the second round

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: FAIL — `InsistentAnnouncementController` does not exist (compile error).

- [ ] **Step 3: Implement the controller**

Create `McpChannelVoice/Services/InsistentAnnouncementController.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Drives an insistent alert: plays the message (High priority) on every targeted online satellite and
// repeats on a gap until acknowledged (out-of-band, via ActiveAlertRegistry) or a safety cap. The
// satellite mics only on local wake, so there is no mic window here — acknowledgment arrives when the
// user wakes a satellite and WyomingSatelliteHost calls ActiveAlertRegistry.Acknowledge.
public sealed class InsistentAnnouncementController(
    SatelliteRegistry registry,
    SatelliteSessionRegistry sessions,
    ITextToSpeech tts,
    VoiceSettings settings,
    ActiveAlertRegistry alerts,
    IMetricsPublisher metrics,
    TimeProvider time,
    ILogger<InsistentAnnouncementController> logger)
{
    public async Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
    {
        var targetIds = ResolveConfigured(request.Target);
        if (targetIds.Count == 0)
        {
            logger.LogWarning(
                "Insistent announce target not found: id={Id} room={Room} all={All}",
                request.Target.SatelliteId, request.Target.Room, request.Target.All);
            throw new AnnounceTargetNotFoundException("No matching satellites for the requested target.");
        }

        var announcementId = Guid.NewGuid().ToString("N");

        if (!targetIds.Any(id => sessions.Get(id) is not null))
        {
            await Task.WhenAll(targetIds.Select(id => SafePublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.AlarmOffline,
                SatelliteId = id,
                Room = registry.GetById(id)?.Room,
                Identity = registry.GetById(id)?.Identity,
                Outcome = "offline"
            })));
            return new AnnounceResponse
            {
                AnnouncementId = announcementId,
                Satellites = targetIds.Select(id => new AnnouncementOutcome { Id = id, Status = "offline" }).ToList()
            };
        }

        var plan = InsistentPlan.Resolve(request.Insistent, settings.Announce.Insistent);
        var handle = new AlertHandle(new CancellationTokenSource(), targetIds);
        alerts.Register(handle);

        _ = Task.Run(() => RunLoopAsync(announcementId, request, plan, handle, targetIds));

        return new AnnounceResponse
        {
            AnnouncementId = announcementId,
            Satellites = targetIds.Select(id =>
                new AnnouncementOutcome { Id = id, Status = sessions.Get(id) is not null ? "started" : "offline" }).ToList()
        };
    }

    private async Task RunLoopAsync(
        string announcementId, AnnounceRequest request, InsistentPlan plan, AlertHandle handle, IReadOnlyList<string> targetIds)
    {
        try
        {
            var buffered = await BufferAudioAsync(request, handle.Token);
            var start = time.GetTimestamp();
            var round = 0;

            while (!handle.Token.IsCancellationRequested
                   && round < plan.MaxRepeats
                   && (plan.MaxDuration is not { } max || time.GetElapsedTime(start) < max))
            {
                foreach (var session in OnlineSessions(targetIds))
                {
                    await session.EnqueuePlaybackAsync(
                        BuildJob(announcementId, buffered, session), settings.Announce.QueueMaxDepth);
                }
                round++;

                try
                {
                    await Task.Delay(plan.Gap, time, handle.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (handle.IsAcknowledged)
            {
                foreach (var session in OnlineSessions(targetIds))
                {
                    session.PreemptCurrent();
                }
                await SafePublishAsync(AlarmEvent(VoiceMetric.AlarmAcknowledged, targetIds, round));
            }
            else
            {
                await SafePublishAsync(AlarmEvent(VoiceMetric.AlarmUnacknowledged, targetIds, round));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insistent alert {Id} loop failed", announcementId);
        }
        finally
        {
            alerts.Discard(handle);
        }
    }

    private async Task<IReadOnlyList<AudioChunk>> BufferAudioAsync(AnnounceRequest request, CancellationToken ct)
    {
        // One synthesis per alert, replayed every round/satellite. Per-satellite voice overrides are not
        // applied to insistent alerts in v1 (single synthesis); the request voice or global voice is used.
        var voice = request.Voice ?? settings.Tts.Wyoming?.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in tts.SynthesizeAsync(request.Text, options, ct))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private PlaybackJob BuildJob(string announcementId, IReadOnlyList<AudioChunk> buffered, SatelliteSession session) =>
        new(
            Label: $"alarm:{announcementId}",
            Priority: AnnouncePriority.High,
            Audio: Replay(buffered),
            OnStarted: _ => SafePublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.AnnouncePlayed,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Priority = AnnouncePriority.High.ToString()
            }),
            OnPreempted: _ => Task.CompletedTask);

    private IEnumerable<SatelliteSession> OnlineSessions(IReadOnlyList<string> targetIds) =>
        targetIds.Select(sessions.Get).Where(s => s is not null).Select(s => s!);

    private VoiceEvent AlarmEvent(VoiceMetric metric, IReadOnlyList<string> targetIds, int rounds)
    {
        var first = targetIds.Count > 0 ? registry.GetById(targetIds[0]) : null;
        return new VoiceEvent
        {
            Metric = metric,
            SatelliteId = targetIds.Count > 0 ? targetIds[0] : null,
            Room = first?.Room,
            Identity = first?.Identity,
            DurationMs = rounds
        };
    }

    private IReadOnlyList<string> ResolveConfigured(AnnounceTarget target)
    {
        if (target.SatelliteIds is { Count: > 0 })
        {
            return target.SatelliteIds.Where(id => registry.GetById(id) is not null).Distinct().ToList();
        }
        if (target.SatelliteId is not null)
        {
            return registry.GetById(target.SatelliteId) is null ? [] : [target.SatelliteId];
        }
        if (target.Room is not null)
        {
            return registry.GetIdsByRoom(target.Room);
        }
        if (target.All == true)
        {
            return registry.GetAllIds();
        }
        return [];
    }

    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }

    private async Task SafePublishAsync(VoiceEvent evt)
    {
        try
        {
            await metrics.PublishAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish alarm metric {Metric}", evt.Metric);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: PASS (4 tests). If a timing test is flaky, increase the `WaitUntilAsync` timeout; do not change the assertions.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/InsistentAnnouncementController.cs \
        Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs
git commit -m "feat(voice): InsistentAnnouncementController repeat-until-ack loop

Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt"
```

---

### Task 5: Wire insistent end-to-end (endpoint, DI, host ack hook)

**Files:**
- Modify: `McpChannelVoice/Services/AnnounceEndpoint.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs`
- Modify: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` (update the 4 existing host constructions; add 1 new test)
- Create: `Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs`

**Interfaces:**
- Consumes: `InsistentAnnouncementController.StartAsync`, `ActiveAlertRegistry.Acknowledge`.
- Produces: `POST /api/voice/announce` with a non-null `insistent` body runs the controller; `WyomingSatelliteHost` gains an `ActiveAlertRegistry alerts` constructor parameter (inserted between `TranscriptDispatcher dispatcher` and `IMetricsPublisher metrics`) and calls `alerts.Acknowledge(session.SatelliteId)` after a successful dispatch.

- [ ] **Step 1: Write the failing endpoint integration test**

Create `Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs`. It mirrors `AnnounceEndToEndTests` (fake satellite TCP server, real host, real controller) but posts an `insistent` request and asserts the announcement **repeats**, then that `ActiveAlertRegistry.Acknowledge` **stops** it:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class InsistentAnnounceE2ETests
{
    [Fact]
    public async Task PostInsistentAnnounce_RepeatsUntilAcknowledged()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var satListener = new TcpListener(IPAddress.Loopback, 0);
        satListener.Start();
        var satPort = ((IPEndPoint)satListener.LocalEndpoint).Port;

        var audioStarts = new System.Collections.Concurrent.ConcurrentQueue<DateTimeOffset>();
        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await satListener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                if (evt.Type == "audio-start")
                {
                    audioStarts.Enqueue(DateTimeOffset.UtcNow);
                }
            }
        }, ct);

        var settings = new VoiceSettings
        {
            WyomingClient = new() { ReconnectDelaySeconds = 1 },
            Announce = new() { Enabled = true, Token = "secret", QueueMaxDepth = 8 },
            Satellites = new()
            {
                ["kitchen-01"] = new()
                {
                    Identity = "household",
                    Room = "Kitchen",
                    WakeWord = "hey_jarvis",
                    Address = $"tcp://127.0.0.1:{satPort}"
                }
            }
        };

        var apiPort = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, apiPort));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(settings.Announce);
        builder.Services.AddSingleton(settings.WyomingClient);
        builder.Services.AddSingleton(new SatelliteRegistry(settings.Satellites));
        builder.Services.AddSingleton<SatelliteSessionRegistry>();
        builder.Services.AddSingleton<ActiveAlertRegistry>();
        builder.Services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => FakeTtsAudio());
        builder.Services.AddSingleton(tts.Object);
        builder.Services.AddSingleton<TranscriptDispatcher>(_ => null!);

        var stt = new Mock<ISpeechToText>();
        builder.Services.AddSingleton(stt.Object);
        builder.Services.AddSingleton<ReplyTextAccumulator>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<VoiceConversationManager>(sp => new VoiceConversationManager(
            Mock.Of<IConversationFactory>(), sp.GetRequiredService<ReplyTextAccumulator>(),
            sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance));
        builder.Services.AddSingleton<AnnouncementService>();
        builder.Services.AddSingleton<InsistentAnnouncementController>();
        builder.Services.AddHostedService<WyomingSatelliteHost>();

        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        await app.StartAsync(ct);

        var sessions = app.Services.GetRequiredService<SatelliteSessionRegistry>();
        await WaitForAsync(() => sessions.Get("kitchen-01") is not null, TimeSpan.FromSeconds(5));

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        http.DefaultRequestHeaders.Add("X-Announce-Token", "secret");

        var response = await http.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new InsistentOptions { GapSeconds = 1, MaxRepeats = 10 }
            }, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // It must REPEAT: wait for at least two audio-start envelopes (gap = 1s).
        await WaitForAsync(() => audioStarts.Count >= 2, TimeSpan.FromSeconds(10));

        // Acknowledge -> the loop stops; no meaningful growth after a couple more gaps.
        app.Services.GetRequiredService<ActiveAlertRegistry>().Acknowledge("kitchen-01").ShouldBeTrue();
        var countAtAck = audioStarts.Count;
        await Task.Delay(TimeSpan.FromSeconds(3), ct); // 3 gaps elapse
        audioStarts.Count.ShouldBeLessThanOrEqualTo(countAtAck + 1); // at most one in-flight round

        await app.StopAsync(CancellationToken.None);
        satListener.Stop();
        await cts.CancelAsync();
        try { await fakeSatellite; } catch { /* cancellation */ }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) { return; }
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    private static async IAsyncEnumerable<AudioChunk> FakeTtsAudio()
    {
        yield return new AudioChunk { Data = new byte[32], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnounceE2ETests"`
Expected: FAIL — the endpoint ignores `Insistent` (plays once, never reaches 2 audio-starts), and/or `InsistentAnnouncementController` is not wired.

- [ ] **Step 3: Route insistent requests in the endpoint**

Modify `McpChannelVoice/Services/AnnounceEndpoint.cs`. Add `InsistentAnnouncementController insistent` to the handler's injected parameters and branch before the existing announce call:

```csharp
        app.MapPost("/api/voice/announce", async (
            AnnounceRequest body,
            HttpContext ctx,
            AnnounceSettings settings,
            AnnouncementService announcer,
            InsistentAnnouncementController insistent) =>
        {
```

Then replace the `try { ... announcer.AnnounceAsync ... }` block with:

```csharp
            try
            {
                // Synthesis and playback run on the satellite's background playback loop, which
                // outlives this HTTP request, so the job runs detached (see CancellationToken.None).
                var response = body.Insistent is not null
                    ? await insistent.StartAsync(body, CancellationToken.None)
                    : await announcer.AnnounceAsync(body, CancellationToken.None);
                return Results.Accepted(value: response);
            }
            catch (AnnounceTargetNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
```

- [ ] **Step 4: Register the new services in DI**

Modify `McpChannelVoice/Modules/ConfigModule.cs`. After the `services.AddSingleton<AnnouncementService>();` line (currently near the bottom), add:

```csharp
        services.AddSingleton(settings.Announce);
        services.AddSingleton<AnnouncementService>();
        services.AddSingleton<ActiveAlertRegistry>();
        services.AddSingleton<InsistentAnnouncementController>();
```

- [ ] **Step 5: Add the ack hook to the host**

Modify `McpChannelVoice/Services/WyomingSatelliteHost.cs`:

5a. Add the constructor parameter (insert between `TranscriptDispatcher dispatcher,` and `IMetricsPublisher metrics,`):

```csharp
public sealed class WyomingSatelliteHost(
    WyomingClientSettings settings,
    VoiceSettings voiceSettings,
    SatelliteRegistry satelliteRegistry,
    SatelliteSessionRegistry sessionRegistry,
    VoiceConversationManager conversationManager,
    ISpeechToText speechToText,
    TranscriptDispatcher dispatcher,
    ActiveAlertRegistry alerts,
    IMetricsPublisher metrics,
    TimeProvider time,
    ILogger<WyomingSatelliteHost> logger) : IHostedService
```

5b. In `TranscribeAndDispatchAsync`, capture the dispatch result and acknowledge any active alert when a real utterance reached the agent. Replace the final `return await dispatcher.DispatchAsync(...)` line with:

```csharp
            var dispatched = await dispatcher.DispatchAsync(session, result, voiceSettings.AgentId, ct);
            if (dispatched)
            {
                // A real utterance reached the agent — treat it as acknowledgment of any active alert on
                // this satellite (the satellite mics only on local wake, so this is the dismissal path).
                alerts.Acknowledge(session.SatelliteId);
            }
            return dispatched;
```

- [ ] **Step 6: Fix the existing host test constructions**

In `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`, every `new WyomingSatelliteHost(...)` call passes positional args `..., dispatcher, publisher.Object, TimeProvider.System, ...`. In **all four** existing tests, insert `new ActiveAlertRegistry(),` between `dispatcher,` and `publisher.Object,`:

```csharp
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance);
```

- [ ] **Step 7: Add a host-level ack test**

Add this `[Fact]` to `WyomingSatelliteHostTests.cs`. It reuses the same fake-satellite harness as `Hub_DialsSatelliteRunsAndStreams_TranscribesAndSendsTranscriptBack` (copy that test's fake-satellite block and STT/dispatcher/registry setup verbatim), with two changes: construct a shared `ActiveAlertRegistry`, pre-register an alert for `kitchen-01`, and assert it is acknowledged once the utterance is dispatched.

```csharp
    [Fact]
    public async Task Hub_DispatchedUtterance_AcknowledgesActiveAlertOnThatSatellite()
    {
        // ... identical fake-satellite + stt + emitter + dispatcher + registry setup as
        // Hub_DialsSatelliteRunsAndStreams_TranscribesAndSendsTranscriptBack ...

        var alerts = new ActiveAlertRegistry();
        using var alertCts = new CancellationTokenSource();
        alerts.Register(new AlertHandle(alertCts, ["kitchen-01"]));

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings
            {
                ReconnectDelaySeconds = 1, SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200, MaxUtteranceMs = 3000, MinSpeechMs = 100
            },
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = false } },
            registry, sessions, manager, stt.Object, dispatcher, alerts, publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); // utterance dispatched
        await WaitForConditionAsync(() => alertCts.IsCancellationRequested, TimeSpan.FromSeconds(5));
        alertCts.IsCancellationRequested.ShouldBeTrue(); // the alert was acknowledged

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try { await fakeSatellite; } catch { /* cancellation */ }
    }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnounceE2ETests|FullyQualifiedName~WyomingSatelliteHostTests"`
Expected: PASS (the new endpoint test, the new host ack test, and all four pre-existing host tests still green).

- [ ] **Step 9: Build the whole solution**

Run: `dotnet build agent.sln`
Expected: Build succeeded (confirms the DI registration and ctor change compile across the app).

- [ ] **Step 10: Commit**

```bash
git add McpChannelVoice/Services/AnnounceEndpoint.cs McpChannelVoice/Modules/ConfigModule.cs \
        McpChannelVoice/Services/WyomingSatelliteHost.cs \
        Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs \
        Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs
git commit -m "feat(voice): wire insistent announce endpoint + wake-word acknowledgment

Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt"
```

---

### Task 6: Teach the agent the alarm-via-calendar idiom

**Files:**
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs`
- Create: `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs`

**Interfaces:**
- Produces: `HomeAssistantPrompt.SystemPrompt` gains an "Alarms & reminders" section describing the calendar-event idiom (summary = message, absolute `start_date_time`, optional `rrule`, `description` = JSON `{target, gapSeconds?, maxRepeats?, maxDurationSeconds?}`).

The agent already creates HA events via the `/ha` VFS (`exec calendar.create_event ...`). This task only adds guidance; no new tools.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs`:

```csharp
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class HomeAssistantPromptTests
{
    [Fact]
    public void SystemPrompt_TeachesAlarmReminderCalendarIdiom()
    {
        var prompt = HomeAssistantPrompt.SystemPrompt;

        prompt.ShouldContain("Alarms & reminders");
        prompt.ShouldContain("calendar.create_event");
        prompt.ShouldContain("description");  // JSON params carried in the event description
        prompt.ShouldContain("rrule");        // recurrence
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantPromptTests"`
Expected: FAIL — the prompt does not contain "Alarms & reminders".

- [ ] **Step 3: Add the prompt section**

Modify `Domain/Prompts/HomeAssistantPrompt.cs` — insert this section into the `SystemPrompt` string, before the closing `### Notes` section:

```
        ### Alarms & reminders

        To set an alarm or reminder, create an event on the alarms calendar
        (`calendar.assistant_alarms`) — do NOT use `/schedules` for human alarms.
        From that entity directory:
        `exec(command="create_event.sh --summary \"Take out the trash\"
              --start_date_time \"2026-06-19 21:30:00\"
              --description \"{\\\"target\\\":{\\\"room\\\":\\\"Kitchen\\\"},\\\"gapSeconds\\\":30,\\\"maxRepeats\\\":5}\"")`

        - `summary` is the spoken message.
        - `start_date_time` is the local wall-clock time. Resolve relative requests
          ("in 20 minutes", "tomorrow at 7") to an absolute date-time yourself; HA
          interprets it in its own timezone (with DST), so you never compute UTC.
        - `rrule` makes it recurring (e.g. `--rrule "FREQ=DAILY"` for every day,
          `FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR` for weekdays).
        - `description` is a JSON object selecting where and how insistently it speaks:
          `target` ({satelliteId | satelliteIds | room | all}), and optional
          `gapSeconds`, `maxRepeats`, `maxDurationSeconds`. The alarm repeats on the
          satellite until the user says "ok nabu" there, or the cap is reached.

        To change or cancel: list with `exec get_events.sh ...`, then
        `exec delete_event.sh ...` / `exec update_event.sh ...` on the event.
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantPromptTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Prompts/HomeAssistantPrompt.cs Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs
git commit -m "feat(voice): teach agent the HA calendar alarm/reminder idiom

Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt"
```

---

### Task 7: Home Assistant provisioning documentation

**Files:**
- Create: `docs/home-assistant-alarms.md`
- Modify: `CLAUDE.md` (HA setup section — add a pointer)

No code, no test. This documents the **one-time** HA-side config that bridges calendar events to the announce endpoint. The shared announce token already flows to the voice container via `env_file: .env` (`Announce__Token=`); the operator sets it and reuses the same value in HA.

- [ ] **Step 1: Write the provisioning doc**

Create `docs/home-assistant-alarms.md`:

```markdown
# Home Assistant → Voice alarms & reminders (provisioning)

Alarms/reminders are HA calendar events that fire an insistent spoken announcement on the
voice satellites. The agent creates the events (via the `/ha` VFS `calendar.create_event`
action); HA fires them; one automation bridges them to the voice hub's announce endpoint.

## One-time setup

1. **Shared token.** Set `Announce__Token=<secret>` in `DockerCompose/.env` (already a key there).
   The `mcp-channel-voice` container reads it via `env_file`. Use the same value below.

2. **Local calendar.** Add a Local Calendar named `assistant_alarms`
   (Settings → Devices & Services → Add Integration → Local Calendar). It appears as
   `calendar.assistant_alarms`.

3. **rest_command** (in HA `configuration.yaml`). Note the **internal** URL/port: the hub listens
   on container port 8080 (published as 6015 on the host), reachable from HA on the compose network
   at `mcp-channel-voice:8080`:

       rest_command:
         voice_announce:
           url: "http://mcp-channel-voice:8080/api/voice/announce"
           method: POST
           headers:
             X-Announce-Token: !secret announce_token
           content_type: "application/json"
           payload: >-
             {"text": {{ summary | to_json }},
              "insistent": true,
              {{ params }} }

   Add `announce_token: <secret>` to HA `secrets.yaml`.

4. **Bridging automation** (fires on every event start of the alarms calendar; forwards the
   event summary as the spoken text and the event description's JSON as target/cap params):

       alias: Voice alarm bridge
       trigger:
         - platform: calendar
           event: start
           entity_id: calendar.assistant_alarms
       action:
         - service: rest_command.voice_announce
           data:
             summary: "{{ trigger.calendar_event.summary }}"
             # description is a JSON object: {"target": {...}, "gapSeconds":.., "maxRepeats":..}
             # strip the outer braces so it can be spliced into the rest_command payload object.
             params: "{{ trigger.calendar_event.description[1:-1] }}"
         # OPTIONAL belt-and-suspenders escalation (fires in parallel, at trigger time):
         # - service: notify.mobile_app_phone
         #   data: { message: "Alarm: {{ trigger.calendar_event.summary }}" }

   The `insistent: true` flag in the rest_command body routes the request to the hub's
   `InsistentAnnouncementController` (repeat-until-acknowledged). The user dismisses by saying
   "ok nabu" at any targeted satellite.

## Notes & limitations (v1)

- Conditional "escalate only if unacknowledged" is not built in — the optional parallel notify above
  fires at trigger time regardless. True ack-gated escalation needs a hub→HA callback (future).
- If no targeted satellite is online when the event fires, the hub records an `AlarmOffline` metric
  and nothing is spoken; the optional parallel notify still reaches another channel.
- Validate against your HA version that the local calendar supports `create_event` (with `rrule`),
  `get_events`, `delete_event`, and `update_event` as services on the calendar entity.
```

- [ ] **Step 2: Point to it from CLAUDE.md**

In `CLAUDE.md`, in the "### Accessing Home Assistant" section (right after the numbered first-run steps), add:

```markdown
For voice alarms/reminders, the agent creates events on a dedicated `calendar.assistant_alarms`
calendar that an HA automation bridges to the voice announce endpoint — see
`docs/home-assistant-alarms.md` for the one-time `rest_command` + automation provisioning.
```

- [ ] **Step 3: Verify the docs render and links resolve**

Run: `git diff --stat` and confirm `docs/home-assistant-alarms.md` is added and `CLAUDE.md` references it. Manually re-read the YAML for indentation.

- [ ] **Step 4: Commit**

```bash
git add docs/home-assistant-alarms.md CLAUDE.md
git commit -m "docs(voice): HA provisioning for calendar-triggered insistent alarms

Claude-Session: https://claude.ai/code/session_01Lm9XcgnUeQXYMK6wUSgtPt"
```

---

## Final verification

- [ ] Run the full voice test surface:
  `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice|FullyQualifiedName~VoiceEnumsTests|FullyQualifiedName~HomeAssistantPromptTests"`
  Expected: all green.
- [ ] `dotnet build agent.sln` — Build succeeded.
- [ ] Confirm `Announce__Token` is documented as the shared secret and the HA URL uses internal port **8080**, not 6015.

## Self-review notes (spec coverage)

- Insistent contract + defaults → Task 1. Alarm metrics → Task 2. Multi-satellite ack registry → Task 3.
  Repeat-until-ack loop (synth-once, cap, ack-preempt, offline) → Task 4. Endpoint/DI/host wake-word ack
  hook → Task 5. Agent prompt idiom → Task 6. HA provisioning (rest_command + automation, internal port,
  shared token) → Task 7.
- Snooze: intentionally absent (removed in spec). In-house scheduler: intentionally untouched.
- Deferred per spec §4 / non-goals: hub-initiated satellite capture ("say anything" dismissal),
  conditional escalation, hub retry-on-reconnect, per-satellite voice override for insistent alerts.
```
