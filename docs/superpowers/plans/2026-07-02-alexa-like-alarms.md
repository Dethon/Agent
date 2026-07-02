# Alexa-like Alarm Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the gap between the current insistent-announce alarms and Alexa-grade alarms/timers: one wake dismisses everything ringing, alarms ring with a tone and volume ramp, "five more minutes" snoozes via LLM context, kitchen timers get a hub-local `/timers` VFS, and unacknowledged alarms escalate through an HA webhook.

**Architecture:** All work is hub-side .NET (`McpChannelVoice` + `Domain` + `Infrastructure`); the Rust satellite is untouched. New audio (tones) is generated PCM at 22 050 Hz mono S16LE (the fixed satellite sink rate). Timers reuse the existing insistent-alert machinery end-to-end; the voice server is already dual-role for the `nabu` agent (its `/mcp` endpoint is in nabu's `mcpServerEndpoints`), so exposing `filesystem://timers` needs no agent-side config.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq + `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, MCP C# SDK (`[McpServerToolType]`/`[McpServerResourceType]`/`[McpServerPromptType]`), System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-07-02-alexa-like-alarms-design.md`

## Global Constraints

- `.cs` files have **NO trailing newline** (`.editorconfig` `insert_final_newline = false`). The pre-commit hook runs `dotnet format` and re-stages **whole files** — make the working tree match the commit you want.
- Commit on the current branch (`alarms-and-reminders`); NEVER switch branches.
- TDD: write the failing test first, run it to see it fail, then implement. Test naming: `{Method}_{Scenario}_{ExpectedResult}`.
- Style: file-scoped namespaces, primary constructors for DI, records for DTOs, LINQ over loops, `TimeProvider` for time, no XML doc comments, comments only for "why".
- Unit tests run standalone: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<Class>"`. Integration tests under `Tests/Integration` need Docker `redis` at most — the ones touched here spin their own TCP fixtures and run standalone.
- All hub-emitted audio MUST be 22 050 Hz mono S16LE — the satellite sink ignores announced rates.
- LLM-facing prompts must reference `domain__filesystem__*` tool leaf names via the `Vfs*Tool.Name` constants — never `fs_*` (enforced by `VfsPromptToolNameConsistencyTests`).

---

### Task 1: `AnnounceKind` + overlapping-alert fix in `ActiveAlertRegistry`

The registry currently maps satellite → **one** handle and `Register` overwrites, so overlapping alarms make the older one undismissable. Change to satellite → **list of handles**; `Acknowledge` cancels all and returns what was dismissed (text + kind) for later snooze use.

**Files:**
- Create: `Domain/DTOs/Voice/AnnounceKind.cs`
- Modify: `Domain/DTOs/Voice/AnnounceRequest.cs`
- Modify: `McpChannelVoice/Services/ActiveAlertRegistry.cs`
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs:54` (AlertHandle construction)
- Test: `Tests/Unit/McpChannelVoice/ActiveAlertRegistryTests.cs`
- Test (adjust): `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs:178`, `Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs:113`

**Interfaces:**
- Produces: `enum AnnounceKind { Alarm, Timer }` (Domain, JSON string-serialized); `AnnounceRequest.Kind` (`AnnounceKind`, default `Alarm`); `AlertHandle(CancellationTokenSource cts, IReadOnlyList<string> satelliteIds, string text, AnnounceKind kind)` with `Text`/`Kind` properties; `record DismissedAlert(string Text, AnnounceKind Kind)`; `ActiveAlertRegistry.Acknowledge(string satelliteId)` now returns `IReadOnlyList<DismissedAlert>` (empty = nothing ringing there).

- [ ] **Step 1: Write the failing tests**

Replace `Tests/Unit/McpChannelVoice/ActiveAlertRegistryTests.cs` with:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ActiveAlertRegistryTests
{
    private static AlertHandle Handle(CancellationTokenSource cts, string text = "alarm", params string[] satellites) =>
        new(cts, satellites, text, AnnounceKind.Alarm);

    [Fact]
    public void Acknowledge_OnAnyTargetedSatellite_CancelsTheSharedAlert()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        var handle = Handle(cts, "alarm", "kitchen-01", "bedroom-01");
        registry.Register(handle);

        var dismissed = registry.Acknowledge("bedroom-01");

        dismissed.ShouldHaveSingleItem();
        handle.IsAcknowledged.ShouldBeTrue();
        handle.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Acknowledge_RemovesEveryTargetEntry_SoASecondAckIsANoOp()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register(Handle(cts, "alarm", "kitchen-01", "bedroom-01"));

        registry.Acknowledge("kitchen-01").ShouldNotBeEmpty();
        registry.Acknowledge("bedroom-01").ShouldBeEmpty(); // already cleared by the first ack
    }

    [Fact]
    public void Acknowledge_UnknownSatellite_ReturnsEmpty()
    {
        var registry = new ActiveAlertRegistry();

        registry.Acknowledge("ghost").ShouldBeEmpty();
    }

    [Fact]
    public void Acknowledge_OverlappingAlertsOnOneSatellite_CancelsAllAndReturnsEachDescription()
    {
        // The Alexa "stop" model: one wake dismisses EVERYTHING ringing on that satellite.
        var registry = new ActiveAlertRegistry();
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        var alarm = new AlertHandle(cts1, ["kitchen-01"], "Take out the trash", AnnounceKind.Alarm);
        var timer = new AlertHandle(cts2, ["kitchen-01"], "pasta", AnnounceKind.Timer);
        registry.Register(alarm);
        registry.Register(timer);

        var dismissed = registry.Acknowledge("kitchen-01");

        dismissed.Count.ShouldBe(2);
        dismissed.ShouldContain(new DismissedAlert("Take out the trash", AnnounceKind.Alarm));
        dismissed.ShouldContain(new DismissedAlert("pasta", AnnounceKind.Timer));
        alarm.Token.IsCancellationRequested.ShouldBeTrue();
        timer.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Discard_RemovesOnlyItsOwnHandle_LeavingOverlappingAlertsActive()
    {
        var registry = new ActiveAlertRegistry();
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        var first = Handle(cts1, "first", "kitchen-01");
        var second = Handle(cts2, "second", "kitchen-01");
        registry.Register(first);
        registry.Register(second);

        registry.Discard(first);

        var dismissed = registry.Acknowledge("kitchen-01");
        dismissed.ShouldHaveSingleItem();
        dismissed[0].Text.ShouldBe("second");
        first.IsAcknowledged.ShouldBeFalse();
    }

    [Fact]
    public void Discard_RemovesEntries_WithoutAcknowledging()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        var handle = Handle(cts, "alarm", "kitchen-01");
        registry.Register(handle);

        registry.Discard(handle);

        registry.Acknowledge("kitchen-01").ShouldBeEmpty();
        handle.IsAcknowledged.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ActiveAlertRegistryTests"`
Expected: FAIL to compile (`AnnounceKind` not defined, `AlertHandle` ctor arity, `Acknowledge` returns bool).

- [ ] **Step 3: Implement**

Create `Domain/DTOs/Voice/AnnounceKind.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Domain.DTOs.Voice;

[JsonConverter(typeof(JsonStringEnumConverter<AnnounceKind>))]
public enum AnnounceKind
{
    Alarm,
    Timer
}
```

In `Domain/DTOs/Voice/AnnounceRequest.cs` add after the `Insistent` property:

```csharp
    public AnnounceKind Kind { get; init; } = AnnounceKind.Alarm;
```

Replace `McpChannelVoice/Services/ActiveAlertRegistry.cs` with:

```csharp
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

public sealed record DismissedAlert(string Text, AnnounceKind Kind);

public sealed class AlertHandle
{
    private readonly CancellationTokenSource _cts;

    public AlertHandle(CancellationTokenSource cts, IReadOnlyList<string> satelliteIds, string text, AnnounceKind kind)
    {
        ArgumentNullException.ThrowIfNull(cts);
        ArgumentNullException.ThrowIfNull(satelliteIds);
        ArgumentNullException.ThrowIfNull(text);
        _cts = cts;
        SatelliteIds = satelliteIds;
        Text = text;
        Kind = kind;
    }

    public IReadOnlyList<string> SatelliteIds { get; }
    public string Text { get; }
    public AnnounceKind Kind { get; }
    public CancellationToken Token => _cts.Token;
    public bool IsAcknowledged { get; private set; }

    public void Acknowledge()
    {
        IsAcknowledged = true;
        _cts.Cancel();
    }
}

// Maps each targeted satellite id to EVERY alert covering it. Acknowledging a satellite cancels all
// of its active alerts (one wake dismisses everything ringing there — the Alexa "stop" model); each
// alert's shared CTS also stops it on its sibling satellites. Returns what was dismissed so the
// caller can hand the descriptions to the snooze context flow.
public sealed class ActiveAlertRegistry
{
    private readonly Dictionary<string, List<AlertHandle>> _bySatellite = new();
    private readonly Lock _gate = new();

    public void Register(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            foreach (var id in handle.SatelliteIds)
            {
                if (!_bySatellite.TryGetValue(id, out var handles))
                {
                    handles = [];
                    _bySatellite[id] = handles;
                }
                handles.Add(handle);
            }
        }
    }

    public IReadOnlyList<DismissedAlert> Acknowledge(string satelliteId)
    {
        List<AlertHandle> acknowledged;
        lock (_gate)
        {
            if (!_bySatellite.TryGetValue(satelliteId, out var handles))
            {
                return [];
            }
            acknowledged = handles.ToList();
        }

        // Acknowledge/Discard outside the lock: Acknowledge cancels a CTS whose continuations may
        // re-enter the registry (Discard from the alert loop's finally).
        foreach (var handle in acknowledged)
        {
            handle.Acknowledge();
            Discard(handle);
        }
        return acknowledged.Select(h => new DismissedAlert(h.Text, h.Kind)).ToList();
    }

    public void Discard(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            foreach (var id in handle.SatelliteIds)
            {
                if (!_bySatellite.TryGetValue(id, out var handles))
                {
                    continue;
                }
                handles.Remove(handle);
                if (handles.Count == 0)
                {
                    _bySatellite.Remove(id);
                }
            }
        }
    }
}
```

In `McpChannelVoice/Services/InsistentAnnouncementController.cs` change the handle construction (line ~54):

```csharp
        var handle = new AlertHandle(new CancellationTokenSource(), targetIds, request.Text, request.Kind);
```

In `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs` line 178 change:

```csharp
        h.Alerts.Acknowledge("kitchen-01").ShouldNotBeEmpty();
```

In `Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs` (~line 113) change the acknowledge assertion the same way:

```csharp
        app.Services.GetRequiredService<ActiveAlertRegistry>().Acknowledge("kitchen-01").ShouldNotBeEmpty();
```

(`WyomingSatelliteHost` calls `alerts.Acknowledge(id);` as a statement — it compiles unchanged with the new return type.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ActiveAlertRegistryTests|FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: PASS (6 + 4 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Voice/AnnounceKind.cs Domain/DTOs/Voice/AnnounceRequest.cs McpChannelVoice/Services/ActiveAlertRegistry.cs McpChannelVoice/Services/InsistentAnnouncementController.cs Tests/Unit/McpChannelVoice/ActiveAlertRegistryTests.cs Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs
git commit -m "fix(voice): one wake dismisses all overlapping alerts on a satellite"
```

---

### Task 2: `AlarmTone` + tone-prefixed rounds + cadence defaults

Every insistent round should ring *tone + message*, with distinct alarm/timer tones. Tighten default cadence (gap 30 s → 15 s, repeats 5 → 12).

**Files:**
- Create: `McpChannelVoice/Services/AlarmTone.cs`
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs` (`BufferAudioAsync`)
- Modify: `McpChannelVoice/Settings/AnnounceSettings.cs` (`InsistentDefaults`)
- Test: `Tests/Unit/McpChannelVoice/AlarmToneTests.cs` (new)
- Test (adjust): `Tests/Unit/McpChannelVoice/InsistentPlanTests.cs`, `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`

**Interfaces:**
- Consumes: `AnnounceKind` (Task 1).
- Produces: `AlarmTone.Pcm(AnnounceKind kind)` → `byte[]` (22 050 Hz mono S16LE); `AlarmTone.Chunk(AnnounceKind kind)` → `AudioChunk`. `InsistentDefaults.GapSeconds` default 15, `MaxRepeats` default 12. Buffered alert audio = `[tone chunk, ...TTS chunks]` — Task 3's ramp scales this whole list per round.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/AlarmToneTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AlarmToneTests
{
    [Fact]
    public void Pcm_AlarmAndTimer_ProduceDistinctPatterns()
    {
        AlarmTone.Pcm(AnnounceKind.Alarm).ShouldNotBe(AlarmTone.Pcm(AnnounceKind.Timer));
    }

    [Fact]
    public void Pcm_IsNonSilentAndBounded()
    {
        var pcm = AlarmTone.Pcm(AnnounceKind.Alarm);

        pcm.Length.ShouldBeGreaterThan(0);
        (pcm.Length % 2).ShouldBe(0); // whole 16-bit samples
        var samples = Enumerable.Range(0, pcm.Length / 2)
            .Select(i => (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)))
            .ToList();
        samples.Max(s => Math.Abs((int)s)).ShouldBeGreaterThan(short.MaxValue / 4); // audible
        samples.Max(s => Math.Abs((int)s)).ShouldBeLessThan(short.MaxValue);        // no clipping
    }

    [Fact]
    public void Chunk_Uses22050MonoS16le()
    {
        var chunk = AlarmTone.Chunk(AnnounceKind.Timer);

        chunk.Format.SampleRateHz.ShouldBe(22_050);
        chunk.Format.SampleWidthBytes.ShouldBe(2);
        chunk.Format.Channels.ShouldBe(1);
    }
}
```

Add to `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs` (uses the existing `BuildHarness`/`WaitUntilAsync` helpers; add this capture helper next to `PumpPlays`):

```csharp
    private static (Task Pump, Func<IReadOnlyList<byte[]>> Chunks) PumpCaptures(
        SatelliteSession session, FakeTimeProvider time)
    {
        var captured = new List<byte[]>();
        var pump = session.RunPlaybackLoopAsync(
            (chunk, _) => { lock (captured) { captured.Add(chunk.Data.ToArray()); } return Task.CompletedTask; },
            CancellationToken.None, time);
        return (pump, () => { lock (captured) { return captured.ToList(); } });
    }

    [Fact]
    public async Task Start_AlarmRound_PlaysToneBeforeSpeech()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, "kitchen-01");
        var (pump, chunks) = PumpCaptures(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "alarm",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => chunks().Count >= 2, TimeSpan.FromSeconds(5)); // tone + TTS chunk
        chunks()[0].ShouldBe(AlarmTone.Pcm(AnnounceKind.Alarm));

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }
```

In `Tests/Unit/McpChannelVoice/InsistentPlanTests.cs` update the default expectations:

- `Resolve_NullOptions_UsesDefaults`: `plan.Gap.ShouldBe(TimeSpan.FromSeconds(15));` and `plan.MaxRepeats.ShouldBe(12);`
- `Resolve_NonPositiveDuration_IsTreatedAsNoDurationCap`: `plan.MaxRepeats.ShouldBe(12);`

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AlarmToneTests|FullyQualifiedName~InsistentPlanTests|FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: FAIL (`AlarmTone` not defined; plan defaults still 30/5; tone-prefix test fails).

- [ ] **Step 3: Implement**

Create `McpChannelVoice/Services/AlarmTone.cs`:

```csharp
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Generated alarm/timer earcons — 22.05 kHz mono S16LE like ListeningChime (the satellite sink is
// fixed at that rate), no asset files. The alarm is an urgent low/high pulse train; the timer is a
// faster, higher triple-beep so the two are audibly distinct.
public static class AlarmTone
{
    private const int SampleRateHz = 22_050;
    private const double Amplitude = 0.5;

    private static readonly AudioFormat _playbackFormat = new()
    {
        SampleRateHz = SampleRateHz,
        SampleWidthBytes = 2,
        Channels = 1
    };

    public static byte[] Pcm(AnnounceKind kind) => kind == AnnounceKind.Timer
        ? Pattern([(1320, 0.09), (0, 0.05), (1320, 0.09), (0, 0.05), (1760, 0.16)])
        : Pattern([(880, 0.14), (0, 0.06), (660, 0.14), (0, 0.06), (880, 0.14), (0, 0.06), (660, 0.18)]);

    public static AudioChunk Chunk(AnnounceKind kind) => new() { Data = Pcm(kind), Format = _playbackFormat };

    // Frequency 0 renders silence. Every voiced segment gets a 10 ms fade in/out to avoid clicks.
    private static byte[] Pattern(IReadOnlyList<(double Freq, double Seconds)> segments)
    {
        var samples = segments
            .SelectMany(seg => Segment(seg.Freq, (int)(SampleRateHz * seg.Seconds)))
            .ToList();
        var pcm = new byte[samples.Count * 2];
        foreach (var (s16, i) in samples.Select((s, i) => (s, i)))
        {
            pcm[i * 2] = (byte)(s16 & 0xFF);
            pcm[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }
        return pcm;
    }

    private static IEnumerable<short> Segment(double freq, int count)
    {
        var fadeSamples = SampleRateHz * 0.01;
        return Enumerable.Range(0, count).Select(i =>
        {
            if (freq <= 0)
            {
                return (short)0;
            }
            var fade = Math.Min(1.0, Math.Min(i, count - i) / fadeSamples);
            var value = Math.Sin(2 * Math.PI * freq * i / SampleRateHz) * fade * Amplitude;
            return (short)(value * short.MaxValue);
        });
    }
}
```

In `McpChannelVoice/Settings/AnnounceSettings.cs` change the defaults:

```csharp
public record InsistentDefaults
{
    public int GapSeconds { get; init; } = 15;
    public int MaxRepeats { get; init; } = 12;
    public int? MaxDurationSeconds { get; init; }
}
```

In `McpChannelVoice/Services/InsistentAnnouncementController.cs`, `BufferAudioAsync` — seed the list with the tone:

```csharp
    private async Task<IReadOnlyList<AudioChunk>> BufferAudioAsync(AnnounceRequest request, CancellationToken ct)
    {
        // One synthesis per alert, replayed every round/satellite. Per-satellite voice overrides are not
        // applied to insistent alerts in v1 (single synthesis); the request voice or global voice is used.
        var voice = request.Voice ?? settings.Tts.Wyoming.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var chunks = new List<AudioChunk> { AlarmTone.Chunk(request.Kind) };
        await foreach (var chunk in tts.SynthesizeAsync(request.Text, options, ct))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AlarmToneTests|FullyQualifiedName~InsistentPlanTests|FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/AlarmTone.cs McpChannelVoice/Services/InsistentAnnouncementController.cs McpChannelVoice/Settings/AnnounceSettings.cs Tests/Unit/McpChannelVoice/AlarmToneTests.cs Tests/Unit/McpChannelVoice/InsistentPlanTests.cs Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs
git commit -m "feat(voice): alarm/timer tone prefix on insistent rounds; tighten cadence defaults"
```

---

### Task 3: `PcmGain` + per-round volume ramp

Rounds start at 50 % gain and reach 100 % by round 4 (configurable; `RampStartPercent: 100` disables).

**Files:**
- Create: `McpChannelVoice/Services/PcmGain.cs`
- Modify: `McpChannelVoice/Settings/AnnounceSettings.cs` (`InsistentDefaults` += ramp knobs)
- Modify: `McpChannelVoice/Services/InsistentPlan.cs`
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs` (loop + `BuildJob` + `Replay`)
- Test: `Tests/Unit/McpChannelVoice/PcmGainTests.cs` (new)
- Test (adjust): `Tests/Unit/McpChannelVoice/InsistentPlanTests.cs`, `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`

**Interfaces:**
- Produces: `PcmGain.Apply(ReadOnlyMemory<byte> pcm, double factor)` → `ReadOnlyMemory<byte>` (identity when `factor >= 1.0`); `InsistentDefaults.RampStartPercent` (int, default 50) and `RampRounds` (int, default 4); `InsistentPlan.GainFor(int round)` (round 0-based) → double in (0, 1].

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/PcmGainTests.cs`:

```csharp
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class PcmGainTests
{
    private static byte[] Pcm(params short[] samples) =>
        samples.SelectMany(s => new[] { (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF) }).ToArray();

    private static short[] Samples(ReadOnlyMemory<byte> pcm) =>
        Enumerable.Range(0, pcm.Length / 2)
            .Select(i => (short)(pcm.Span[i * 2] | (pcm.Span[i * 2 + 1] << 8)))
            .ToArray();

    [Fact]
    public void Apply_HalfGain_HalvesSamples()
    {
        var scaled = PcmGain.Apply(Pcm(1000, -1000, 0), 0.5);

        Samples(scaled).ShouldBe(new short[] { 500, -500, 0 });
    }

    [Fact]
    public void Apply_FullGain_ReturnsInputUnchanged()
    {
        var input = Pcm(1000, -1000);

        PcmGain.Apply(input, 1.0).ToArray().ShouldBe(input);
    }

    [Fact]
    public void Apply_NeverOverflows()
    {
        var scaled = PcmGain.Apply(Pcm(short.MaxValue, short.MinValue), 0.99);

        var samples = Samples(scaled);
        samples[0].ShouldBeGreaterThan((short)0);
        samples[1].ShouldBeLessThan((short)0);
    }
}
```

Add to `Tests/Unit/McpChannelVoice/InsistentPlanTests.cs`:

```csharp
    [Fact]
    public void GainFor_DefaultRamp_RisesFromHalfToFullByRampRounds()
    {
        var plan = InsistentPlan.Resolve(null, _defaults); // RampStartPercent 50, RampRounds 4

        plan.GainFor(0).ShouldBe(0.5, 0.001);
        plan.GainFor(1).ShouldBe(0.5 + 0.5 / 3, 0.001);
        plan.GainFor(3).ShouldBe(1.0, 0.001);
        plan.GainFor(10).ShouldBe(1.0, 0.001);
    }

    [Fact]
    public void GainFor_RampStart100_DisablesRamp()
    {
        var plan = InsistentPlan.Resolve(null, new InsistentDefaults { RampStartPercent = 100 });

        plan.GainFor(0).ShouldBe(1.0, 0.001);
    }
```

In `InsistentAnnouncementControllerTests`: change `Start_AlarmRound_PlaysToneBeforeSpeech` (Task 2) to expect the round-0 ramp:

```csharp
        chunks()[0].ShouldBe(PcmGain.Apply(AlarmTone.Pcm(AnnounceKind.Alarm), 0.5).ToArray());
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PcmGainTests|FullyQualifiedName~InsistentPlanTests|FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: FAIL (`PcmGain`/`GainFor`/`RampStartPercent` not defined).

- [ ] **Step 3: Implement**

Create `McpChannelVoice/Services/PcmGain.cs`:

```csharp
namespace McpChannelVoice.Services;

// Saturating gain over 16-bit little-endian mono PCM. Factor >= 1 returns the input untouched so
// the fully-ramped rounds replay the original buffer without a copy.
public static class PcmGain
{
    public static ReadOnlyMemory<byte> Apply(ReadOnlyMemory<byte> pcm, double factor)
    {
        if (factor >= 1.0)
        {
            return pcm;
        }

        var src = pcm.Span;
        var dst = new byte[pcm.Length];
        for (var i = 0; i + 1 < src.Length; i += 2)
        {
            var sample = (short)(src[i] | (src[i + 1] << 8));
            var scaled = (short)Math.Clamp((int)Math.Round(sample * factor), short.MinValue, short.MaxValue);
            dst[i] = (byte)(scaled & 0xFF);
            dst[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
        return dst;
    }
}
```

In `McpChannelVoice/Settings/AnnounceSettings.cs` extend `InsistentDefaults`:

```csharp
public record InsistentDefaults
{
    public int GapSeconds { get; init; } = 15;
    public int MaxRepeats { get; init; } = 12;
    public int? MaxDurationSeconds { get; init; }
    // Round-1 playback gain in percent, ramping linearly to 100 by RampRounds. 100 disables the ramp.
    public int RampStartPercent { get; init; } = 50;
    public int RampRounds { get; init; } = 4;
}
```

Replace `McpChannelVoice/Services/InsistentPlan.cs` with:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public readonly record struct InsistentPlan(
    TimeSpan Gap, int MaxRepeats, TimeSpan? MaxDuration, double RampStart, int RampRounds)
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

        var rampStart = Math.Clamp(defaults.RampStartPercent, 1, 100) / 100.0;
        return new InsistentPlan(gap, maxRepeats, maxDuration, rampStart, Math.Max(1, defaults.RampRounds));
    }

    // Playback gain for a 0-based round: linear from RampStart to 1.0 across the first RampRounds
    // rounds, full volume after.
    public double GainFor(int round) =>
        RampStart >= 1.0 || RampRounds <= 1
            ? 1.0
            : Math.Min(1.0, RampStart + (1.0 - RampStart) * round / (RampRounds - 1));
}
```

In `McpChannelVoice/Services/InsistentAnnouncementController.cs`:

1. In `RunLoopAsync`, pass the round gain into the job (the `foreach` at the top of the `while` loop):

```csharp
                var gain = plan.GainFor(round);
                foreach (var session in OnlineSessions(targetIds))
                {
                    await session.EnqueuePlaybackAsync(
                        BuildJob(announcementId, buffered, session, gain), settings.Announce.QueueMaxDepth);
                }
```

2. Thread the gain through `BuildJob` and `Replay`:

```csharp
    private PlaybackJob BuildJob(
        string announcementId, IReadOnlyList<AudioChunk> buffered, SatelliteSession session, double gain) =>
        new(
            Label: $"alarm:{announcementId}",
            Priority: AnnouncePriority.High,
            Audio: Replay(buffered, gain),
            OnStarted: _ => SafePublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.AnnouncePlayed,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Priority = AnnouncePriority.High.ToString()
            }),
            OnPreempted: _ => Task.CompletedTask);
```

```csharp
    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks, double gain)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk with { Data = PcmGain.Apply(chunk.Data, gain) };
        }
        await Task.CompletedTask;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PcmGainTests|FullyQualifiedName~InsistentPlanTests|FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/PcmGain.cs McpChannelVoice/Services/InsistentPlan.cs McpChannelVoice/Services/InsistentAnnouncementController.cs McpChannelVoice/Settings/AnnounceSettings.cs Tests/Unit/McpChannelVoice/PcmGainTests.cs Tests/Unit/McpChannelVoice/InsistentPlanTests.cs Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs
git commit -m "feat(voice): per-round volume ramp on insistent alerts"
```

---

### Task 4: `DismissedAlert` protocol rails (notification → ChannelMessage → message prefix)

The agent-side path that turns a dismissed-alert description into LLM context. Mirrors how `Location`/`SatelliteId` already ride the protocol.

**Files:**
- Modify: `Domain/DTOs/Channel/ChannelMessageNotification.cs`
- Modify: `Domain/DTOs/ChannelMessage.cs`
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs:88-97` (notification → `ChannelMessage` mapping)
- Modify: `Domain/Extensions/ChatMessageExtensions.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs:195` (`BuildUserMessageAsync`)
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs:77-113` (prefix rendering)
- Test: `Tests/Unit/Infrastructure/ChatClients/OpenRouterChatClientPrefixTests.cs`, `Tests/Unit/Domain/ChatMessageSerializationTests.cs`

**Interfaces:**
- Produces: `ChannelMessageNotification.DismissedAlert` and `ChannelMessage.DismissedAlert` (`string?`; a human-readable description like `alarm "Take out the trash"`, composed by the voice channel in Task 5); `ChatMessage` extensions `GetDismissedAlert()`/`SetDismissedAlert(string?)`; the chat client renders `[The user just dismissed the {description}]` on its own line before the existing sender/time prefix.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/Unit/Infrastructure/ChatClients/OpenRouterChatClientPrefixTests.cs`:

```csharp
    [Fact]
    public async Task GetStreamingResponseAsync_WithDismissedAlert_PrefixesDismissalContext()
    {
        var msg = new ChatMessage(ChatRole.User, "five more minutes");
        msg.SetSenderId("household");
        msg.SetDismissedAlert("alarm \"Take out the trash\"");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("[The user just dismissed the alarm \"Take out the trash\"]\nMessage from household:");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutDismissedAlert_NoDismissalPrefix()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldNotContain("dismissed");
    }
```

Add to `Tests/Unit/Domain/ChatMessageSerializationTests.cs`:

```csharp
    [Fact]
    public void GetDismissedAlert_JsonElementValue_RoundTrips()
    {
        // After a thread reload AdditionalProperties values come back as JsonElement, not string.
        var message = new ChatMessage(ChatRole.User, "five more minutes");
        message.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["DismissedAlert"] = JsonSerializer.SerializeToElement("alarm \"trash\"")
        };

        message.GetDismissedAlert().ShouldBe("alarm \"trash\"");
    }

    [Fact]
    public void SetDismissedAlert_ThenGet_ReturnsValue()
    {
        var message = new ChatMessage(ChatRole.User, "hi");

        message.SetDismissedAlert("timer \"pasta\"");

        message.GetDismissedAlert().ShouldBe("timer \"pasta\"");
    }
```

(Match the existing file's usings; it already exercises `AdditionalProperties` round-trips for `MemoryContext`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClientPrefixTests|FullyQualifiedName~ChatMessageSerializationTests"`
Expected: FAIL to compile (`SetDismissedAlert`/`GetDismissedAlert` not defined).

- [ ] **Step 3: Implement**

`Domain/DTOs/Channel/ChannelMessageNotification.cs` — add after `SatelliteId`:

```csharp
    // Optional description of an alert (alarm/timer) the user dismissed just before speaking, e.g.
    // 'alarm "Take out the trash"'. Voice-only, like Location/SatelliteId; enables LLM-mediated snooze.
    public string? DismissedAlert { get; init; }
```

`Domain/DTOs/ChannelMessage.cs` — add after `SatelliteId`:

```csharp
    public string? DismissedAlert { get; init; }
```

`Infrastructure/Clients/Channels/McpChannelConnection.cs` — in the `ChannelMessage` construction that maps the message notification (around line 96), add:

```csharp
            SatelliteId = notification.SatelliteId,
            DismissedAlert = notification.DismissedAlert
```

`Domain/Extensions/ChatMessageExtensions.cs` — add the key constant next to the others:

```csharp
    private const string DismissedAlertKey = "DismissedAlert";
```

and the accessors next to `GetLocation`/`SetLocation` (same shape):

```csharp
        public string? GetDismissedAlert()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(DismissedAlertKey);
            return value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                _ => null
            };
        }

        public void SetDismissedAlert(string? dismissedAlert)
        {
            if (string.IsNullOrWhiteSpace(dismissedAlert))
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[DismissedAlertKey] = dismissedAlert;
        }
```

`Domain/Monitor/ChatMonitor.cs` — in `BuildUserMessageAsync`, after `userMessage.SetSatelliteId(message.SatelliteId);`:

```csharp
        userMessage.SetDismissedAlert(message.DismissedAlert);
```

`Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` — in `GetStreamingResponseAsync`'s transform:

1. After `var satelliteId = newMessage.GetSatelliteId();` add:

```csharp
            var dismissedAlert = newMessage.GetDismissedAlert();
```

2. Extend the guard so a dismissal alone still renders:

```csharp
            if (newMessage.Role == ChatRole.User && (msgSender is not null || timestamp is not null || dismissedAlert is not null))
```

3. After the `var prefix = (senderSegment, timestamp) switch { ... };` expression, before `newMessage.Contents = ...`:

```csharp
                if (!string.IsNullOrWhiteSpace(dismissedAlert))
                {
                    prefix = $"[The user just dismissed the {dismissedAlert}]\n{prefix}";
                }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClientPrefixTests|FullyQualifiedName~ChatMessageSerializationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/ChannelMessageNotification.cs Domain/DTOs/ChannelMessage.cs Infrastructure/Clients/Channels/McpChannelConnection.cs Domain/Extensions/ChatMessageExtensions.cs Domain/Monitor/ChatMonitor.cs Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Infrastructure/ChatClients/OpenRouterChatClientPrefixTests.cs Tests/Unit/Domain/ChatMessageSerializationTests.cs
git commit -m "feat(agent): DismissedAlert context rides the channel protocol into the LLM prefix"
```

---

### Task 5: Voice-side snooze stash + prompt teaching

When a wake dismisses alerts, remember the descriptions on the session for 60 s; the next dispatched transcript carries them so "five more minutes" works in any language.

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs` (stash)
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs` (wake-ack + fallback sites)
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs` (+`TimeProvider`, consume stash)
- Modify: `McpChannelVoice/Services/ChannelNotificationEmitter.cs` (+`dismissedAlert` param)
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (dispatcher registration)
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs` (snooze idiom)
- Test: `Tests/Unit/McpChannelVoice/SatelliteSessionDismissStashTests.cs` (new), `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, `Tests/Unit/McpChannelVoice/CapturingEmitter.cs`, `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs`

**Interfaces:**
- Consumes: `ActiveAlertRegistry.Acknowledge` → `IReadOnlyList<DismissedAlert>` (Task 1); `ChannelMessageNotification.DismissedAlert` (Task 4).
- Produces: `SatelliteSession.NoteDismissedAlert(string description, DateTimeOffset now)` and `SatelliteSession.TryConsumeDismissedAlert(DateTimeOffset now)` → `string?` (single-use, 60 s window); `TranscriptDispatcher` ctor gains `TimeProvider timeProvider` (after `confidenceThreshold`); `ChannelNotificationEmitter.EmitMessageNotificationAsync(..., string? satelliteId, string? dismissedAlert, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/SatelliteSessionDismissStashTests.cs`:

```csharp
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionDismissStashTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public void TryConsumeDismissedAlert_WithinWindow_ReturnsOnceThenNull()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;
        session.NoteDismissedAlert("alarm \"trash\"", now);

        session.TryConsumeDismissedAlert(now.AddSeconds(10)).ShouldBe("alarm \"trash\"");
        session.TryConsumeDismissedAlert(now.AddSeconds(11)).ShouldBeNull(); // single-use
    }

    [Fact]
    public void TryConsumeDismissedAlert_AfterWindow_ReturnsNull()
    {
        var session = Session();
        var now = DateTimeOffset.UtcNow;
        session.NoteDismissedAlert("alarm \"trash\"", now);

        session.TryConsumeDismissedAlert(now.AddSeconds(61)).ShouldBeNull();
    }

    [Fact]
    public void TryConsumeDismissedAlert_NothingStashed_ReturnsNull()
    {
        Session().TryConsumeDismissedAlert(DateTimeOffset.UtcNow).ShouldBeNull();
    }
}
```

In `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`:

1. The `Build()` helper's dispatcher construction gains a `FakeTimeProvider` (declare `var time = new FakeTimeProvider(DateTimeOffset.UtcNow);` and reuse it for the manager):

```csharp
        var sut = new TranscriptDispatcher(
            emitter, Mock.Of<IMetricsPublisher>(), manager,
            confidenceThreshold: 0.5, time, NullLogger<TranscriptDispatcher>.Instance);
```

2. Add:

```csharp
    [Fact]
    public async Task DispatchAsync_AfterDismissal_EmitsDismissedAlertOnce()
    {
        var (sut, _, emitter) = Build();
        var session = Session();
        session.NoteDismissedAlert("alarm \"trash\"", DateTimeOffset.UtcNow);

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "five more minutes", Confidence = 0.9 }, "agent-1", default);
        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "thanks", Confidence = 0.9 }, "agent-1", default);

        emitter.Captured.Count.ShouldBe(2);
        emitter.Captured[0].DismissedAlert.ShouldBe("alarm \"trash\"");
        emitter.Captured[1].DismissedAlert.ShouldBeNull(); // consumed by the first dispatch
    }
```

Note: `Build()` currently returns a tuple without the time provider; if the fake time must be observable, extend the tuple — for this test wall-clock construction (`FakeTimeProvider(DateTimeOffset.UtcNow)`) plus `DateTimeOffset.UtcNow` stashing is within the 60 s window, so no extension is needed.

3. Update `Tests/Unit/McpChannelVoice/CapturingEmitter.cs` to the new override signature:

```csharp
    public override Task EmitMessageNotificationAsync(
        string conversationId, string sender, string content, string? agentId, string? location,
        string? satelliteId, string? dismissedAlert, CancellationToken ct = default)
    {
        Captured.Add(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Location = location,
            SatelliteId = satelliteId,
            DismissedAlert = dismissedAlert
        });
        return Task.CompletedTask;
    }
```

In `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs` add:

```csharp
    [Fact]
    public void Prompt_TeachesSnoozeAfterDismissal()
    {
        HomeAssistantPrompt.Prompt.ShouldContain("just dismissed");
        HomeAssistantPrompt.Prompt.ShouldContain("new one-shot");
    }
```

(Adapt the member access to how the existing tests in that file reference the prompt text — same accessor they already use.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionDismissStashTests|FullyQualifiedName~TranscriptDispatcherTests|FullyQualifiedName~HomeAssistantPromptTests"`
Expected: FAIL to compile (stash methods, ctor arity, emitter signature).

- [ ] **Step 3: Implement**

`McpChannelVoice/Services/SatelliteSession.cs` — add fields near the other private state and the two methods with the public members:

```csharp
    private static readonly TimeSpan _snoozeWindow = TimeSpan.FromSeconds(60);
    private readonly Lock _dismissGate = new();
    private string? _dismissedAlert;
    private DateTimeOffset _dismissedAt;
```

```csharp
    // Wake-word dismissal context for LLM-mediated snooze: the host stashes what was dismissed; the
    // next dispatched transcript within the window consumes it (single-use).
    public void NoteDismissedAlert(string description, DateTimeOffset now)
    {
        lock (_dismissGate)
        {
            _dismissedAlert = description;
            _dismissedAt = now;
        }
    }

    public string? TryConsumeDismissedAlert(DateTimeOffset now)
    {
        lock (_dismissGate)
        {
            var value = _dismissedAlert is not null && now - _dismissedAt <= _snoozeWindow
                ? _dismissedAlert
                : null;
            _dismissedAlert = null;
            return value;
        }
    }
```

`McpChannelVoice/Services/WyomingSatelliteHost.cs`:

1. Wake site (the `run-pipeline`/`audio-start` case):

```csharp
                    case "run-pipeline":
                    case "audio-start":
                        // Waking the satellite during an active alert dismisses it — no spoken command
                        // needed (the satellite mics only on local wake).
                        NoteDismissals(session, alerts.Acknowledge(id));
                        coordinator.OnWake();
                        break;
```

2. Fallback site in `TranscribeAndDispatchAsync`:

```csharp
            var dispatched = await dispatcher.DispatchAsync(session, result, voiceSettings.AgentId, ct);
            if (dispatched)
            {
                // Wake (above) is the primary dismissal path; this is a harmless fallback for turns
                // where a wake event was not observed. The registry makes a second Acknowledge a no-op.
                // Runs AFTER this dispatch, so its snooze context lands on the NEXT transcript.
                NoteDismissals(session, alerts.Acknowledge(session.SatelliteId));
            }
            return dispatched;
```

3. Add the helper (near the other private helpers):

```csharp
    private void NoteDismissals(SatelliteSession session, IReadOnlyList<DismissedAlert> dismissed)
    {
        if (dismissed.Count == 0)
        {
            return;
        }
        var description = string.Join(" and ", dismissed.Select(d =>
            $"{d.Kind.ToString().ToLowerInvariant()} \"{d.Text}\""));
        session.NoteDismissedAlert(description, time.GetUtcNow());
    }
```

`McpChannelVoice/Services/TranscriptDispatcher.cs` — primary constructor gains `TimeProvider timeProvider` after `double confidenceThreshold`; in `DispatchAsync`, after the conversation id is resolved and before the emit:

```csharp
        var dismissedAlert = session.TryConsumeDismissedAlert(timeProvider.GetUtcNow());

        await emitter.EmitMessageNotificationAsync(
            conversationId,
            session.Config.Identity,
            transcript.Text,
            agentId,
            session.Config.DisplayLocation,
            session.SatelliteId,
            dismissedAlert,
            ct);
```

`McpChannelVoice/Services/ChannelNotificationEmitter.cs` — `EmitMessageNotificationAsync` gains `string? dismissedAlert` before the cancellation token and maps it:

```csharp
    public virtual async Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string? agentId,
        string? location,
        string? satelliteId,
        string? dismissedAlert,
        CancellationToken cancellationToken = default)
```

with `DismissedAlert = dismissedAlert,` added to the payload construction.

`McpChannelVoice/Modules/ConfigModule.cs` — the `TranscriptDispatcher` registration gains the time provider:

```csharp
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                settings.ConfidenceThreshold,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()))
```

Fix any other `EmitMessageNotificationAsync` call sites in `McpChannelVoice` (search for the method name; pass `null` for `dismissedAlert` where no dismissal context exists).

`Domain/Prompts/HomeAssistantPrompt.cs` — extend the "Alarms & reminders" section (after the "To change or cancel" line):

```
        Snooze: when the message context says the user just dismissed an alarm and they ask to
        snooze or be reminded again ("five more minutes"), create a new one-shot event on the
        alarms calendar at the requested offset with the same summary and description.
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionDismissStashTests|FullyQualifiedName~TranscriptDispatcherTests|FullyQualifiedName~HomeAssistantPromptTests"`
Expected: PASS. Then build the full solution (`dotnet build agent.sln`) — integration tests referencing the emitter must compile.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/Services/ChannelNotificationEmitter.cs McpChannelVoice/Modules/ConfigModule.cs Domain/Prompts/HomeAssistantPrompt.cs Tests/Unit/McpChannelVoice/SatelliteSessionDismissStashTests.cs Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs Tests/Unit/McpChannelVoice/CapturingEmitter.cs Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs
git commit -m "feat(voice): snooze context — dismissed alerts ride the next transcript"
```

---

### Task 6: `/timers` Domain engine (`ITimerStore`, `TimerPath`, `TimerFileSystem`) + in-memory store

The VFS backend for `filesystem://timers`. The in-memory store lands in `Infrastructure/Timers/` so the journey tests exercise real dependencies (per testing rules).

**Files:**
- Create: `Domain/DTOs/Voice/ArmedTimer.cs`, `Domain/Contracts/ITimerStore.cs`, `Domain/Tools/Timers/Vfs/TimerPath.cs`, `Domain/Tools/Timers/Vfs/TimerFileSystem.cs`
- Create: `Infrastructure/Timers/InMemoryTimerStore.cs`
- Test: `Tests/Unit/Domain/Timers/Vfs/TimerFileSystemJourneyTests.cs` (new), `Tests/Unit/Infrastructure/InMemoryTimerStoreTests.cs` (new)

**Interfaces:**
- Consumes: `AnnounceTarget` (existing), `IFileSystemBackend` + `FsResult<T>` (existing), `GlobRegex.CompileMatcher`, `VfsContentSearch` (existing helpers used by `ScheduleFileSystem`).
- Produces:

```csharp
public record ArmedTimer
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public required AnnounceTarget Target { get; init; }
    public required int DurationSeconds { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime FiresAtUtc { get; init; }
}

public interface ITimerStore
{
    Task ArmAsync(ArmedTimer timer, CancellationToken ct = default);
    Task<ArmedTimer?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ArmedTimer>> ListAsync(CancellationToken ct = default);
    Task<bool> CancelAsync(string id, CancellationToken ct = default);
    // Atomically removes and returns every timer due as of asOfUtc (fire-once semantics).
    Task<IReadOnlyList<ArmedTimer>> TakeDueAsync(DateTime asOfUtc, CancellationToken ct = default);
}
```

`TimerFileSystem(ITimerStore store, TimeProvider timeProvider) : IFileSystemBackend`, `FilesystemName = "timers"`. Layout: `/<timerId>/timer.json` (create-only spec `{durationSeconds, text?, target}`), `/<timerId>/status.json` (read-only `{remainingSeconds, firesAt}` — `firesAt` in the operating zone). Delete = cancel (timer **directory** only). Edit/move/copy/exec unsupported; chunk streaming throws `NotSupportedException`.

- [ ] **Step 1: Write the failing store tests**

Create `Tests/Unit/Infrastructure/InMemoryTimerStoreTests.cs`:

```csharp
using Domain.DTOs.Voice;
using Infrastructure.Timers;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class InMemoryTimerStoreTests
{
    private static ArmedTimer Timer(string id, DateTime firesAtUtc) => new()
    {
        Id = id,
        Target = new AnnounceTarget { Room = "Kitchen" },
        DurationSeconds = 300,
        CreatedAtUtc = firesAtUtc.AddSeconds(-300),
        FiresAtUtc = firesAtUtc
    };

    [Fact]
    public async Task TakeDueAsync_RemovesAndReturnsOnlyDueTimers()
    {
        var store = new InMemoryTimerStore();
        var now = DateTime.UtcNow;
        await store.ArmAsync(Timer("due", now.AddSeconds(-1)));
        await store.ArmAsync(Timer("later", now.AddMinutes(5)));

        var due = await store.TakeDueAsync(now);

        due.ShouldHaveSingleItem();
        due[0].Id.ShouldBe("due");
        (await store.GetAsync("due")).ShouldBeNull();          // removed — fires once
        (await store.GetAsync("later")).ShouldNotBeNull();
        (await store.TakeDueAsync(now)).ShouldBeEmpty();       // second take is empty
    }

    [Fact]
    public async Task CancelAsync_RemovesTimer_AndReportsMisses()
    {
        var store = new InMemoryTimerStore();
        await store.ArmAsync(Timer("pasta", DateTime.UtcNow.AddMinutes(5)));

        (await store.CancelAsync("pasta")).ShouldBeTrue();
        (await store.CancelAsync("pasta")).ShouldBeFalse();
        (await store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_OrdersByFireTime()
    {
        var store = new InMemoryTimerStore();
        var now = DateTime.UtcNow;
        await store.ArmAsync(Timer("second", now.AddMinutes(10)));
        await store.ArmAsync(Timer("first", now.AddMinutes(1)));

        (await store.ListAsync()).Select(t => t.Id).ShouldBe(["first", "second"]);
    }
}
```

- [ ] **Step 2: Run store tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InMemoryTimerStoreTests"`
Expected: FAIL to compile (`ArmedTimer`, `ITimerStore`, `InMemoryTimerStore` not defined).

- [ ] **Step 3: Implement DTO, contract, store**

Create `Domain/DTOs/Voice/ArmedTimer.cs` and `Domain/Contracts/ITimerStore.cs` exactly as in the Interfaces block above (namespaces `Domain.DTOs.Voice` / `Domain.Contracts`; the contract file needs `using Domain.DTOs.Voice;`).

Create `Infrastructure/Timers/InMemoryTimerStore.cs`:

```csharp
using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Timers;

// Timers are deliberately non-durable (kitchen-scale countdowns; spec defers durability), so a
// process-local map is the whole store.
public sealed class InMemoryTimerStore : ITimerStore
{
    private readonly ConcurrentDictionary<string, ArmedTimer> _timers = new();

    public Task ArmAsync(ArmedTimer timer, CancellationToken ct = default)
    {
        _timers[timer.Id] = timer;
        return Task.CompletedTask;
    }

    public Task<ArmedTimer?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_timers.GetValueOrDefault(id));

    public Task<IReadOnlyList<ArmedTimer>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArmedTimer>>(
            _timers.Values.OrderBy(t => t.FiresAtUtc).ThenBy(t => t.Id, StringComparer.Ordinal).ToList());

    public Task<bool> CancelAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_timers.TryRemove(id, out _));

    public Task<IReadOnlyList<ArmedTimer>> TakeDueAsync(DateTime asOfUtc, CancellationToken ct = default)
    {
        var due = _timers.Values
            .Where(t => t.FiresAtUtc <= asOfUtc)
            .OrderBy(t => t.FiresAtUtc)
            .Where(t => _timers.TryRemove(t.Id, out _)) // atomic claim per timer
            .ToList();
        return Task.FromResult<IReadOnlyList<ArmedTimer>>(due);
    }
}
```

- [ ] **Step 4: Run store tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InMemoryTimerStoreTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing journey tests**

Create `Tests/Unit/Domain/Timers/Vfs/TimerFileSystemJourneyTests.cs` (mirror the style of `ScheduleFileSystemJourneyTests`; real store + `FakeTimeProvider` with `LocalTimeZone` set so zone rendering is observable):

```csharp
using Domain.Tools.Timers.Vfs;
using Domain.DTOs.FileSystem;
using Infrastructure.Timers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Timers.Vfs;

public class TimerFileSystemJourneyTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static (TimerFileSystem Fs, InMemoryTimerStore Store, FakeTimeProvider Time) Build()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(_madrid);
        var store = new InMemoryTimerStore();
        return (new TimerFileSystem(store, time), store, time);
    }

    private const string PastaSpec = """
        {"durationSeconds": 300, "text": "pasta is ready", "target": {"room": "Kitchen"}}
        """;

    [Fact]
    public async Task CreateReadStatusCancel_FullJourney()
    {
        var (fs, _, time) = Build();

        var created = await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, default);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        var glob = (FsResult<FsGlobResult>.Ok)await fs.GlobAsync("/", "**", default);
        glob.Value.Entries.ShouldBe(["/pasta/", "/pasta/status.json", "/pasta/timer.json"]);

        var spec = (FsResult<FsReadResult>.Ok)await fs.ReadAsync("/pasta/timer.json", null, null, default);
        spec.Value.Content.ShouldContain("\"durationSeconds\": 300");
        spec.Value.Content.ShouldContain("pasta is ready");

        time.Advance(TimeSpan.FromSeconds(100));
        var status = (FsResult<FsReadResult>.Ok)await fs.ReadAsync("/pasta/status.json", null, null, default);
        status.Value.Content.ShouldContain("\"remainingSeconds\": 200");
        status.Value.Content.ShouldContain("+02:00"); // firesAt rendered in the operating zone (CEST)

        var deleted = await fs.DeleteAsync("/pasta", default);
        deleted.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        (await fs.ReadAsync("/pasta/timer.json", null, null, default))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>();
    }

    [Fact]
    public async Task Create_InvalidDuration_IsRejected()
    {
        var (fs, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 0, "target": {"room": "Kitchen"}}""", false, true, default);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("durationSeconds");
    }

    [Fact]
    public async Task Create_MissingTarget_IsRejected()
    {
        var (fs, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 60}""", false, true, default);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("target");
    }

    [Fact]
    public async Task Create_DuplicateId_IsRejected()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, default);

        var result = await fs.CreateAsync("/pasta/timer.json", PastaSpec, true, true, default);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Create_WrongPathShape_IsRejected()
    {
        var (fs, _, _) = Build();

        var result = await fs.CreateAsync("/pasta.json", PastaSpec, false, true, default);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Edit_IsUnsupported_TimersAreImmutable()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, default);

        var result = await fs.EditAsync("/pasta/timer.json",
            [new TextEdit { OldString = "300", NewString = "600" }], default);

        var err = result.ShouldBeOfType<FsResult<FsEditResult>.Err>();
        err.Error.Message.ShouldContain("immutable");
    }

    [Fact]
    public async Task Delete_TimerJsonFile_IsRejected_DirIsTheUnit()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, default);

        (await fs.DeleteAsync("/pasta/timer.json", default))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
    }

    [Fact]
    public async Task Search_FindsTimerSpecContent()
    {
        var (fs, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, default);

        var result = (FsResult<FsSearchResult>.Ok)await fs.SearchAsync(
            "pasta is ready", regex: false, null, null, null, 10, 0,
            VfsTextSearchOutputMode.Content, default);

        result.Value.TotalMatches.ShouldBe(1);
        result.Value.Results[0].FilePath.ShouldBe("/pasta/timer.json");
    }
}
```

Adapt the exact `FsResult` pattern-matching / error-accessor syntax to what `ScheduleFileSystemJourneyTests` uses (`Ok`/`Err` member names, `TextEdit` initializer, `VfsTextSearchOutputMode` namespace) — read that file first and mirror it.

- [ ] **Step 6: Run journey tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TimerFileSystemJourneyTests"`
Expected: FAIL to compile (`TimerFileSystem` not defined).

- [ ] **Step 7: Implement `TimerPath` and `TimerFileSystem`**

Create `Domain/Tools/Timers/Vfs/TimerPath.cs`:

```csharp
namespace Domain.Tools.Timers.Vfs;

public enum TimerNodeKind
{
    Root, TimerDir, TimerFile, StatusFile, Unknown
}

public sealed record TimerNode(TimerNodeKind Kind, string? TimerId);

public static class TimerPath
{
    public const string TimerFileName = "timer.json";
    public const string StatusFileName = "status.json";

    public static TimerNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Array.Exists(segments, s => s is "." or ".."))
        {
            return new TimerNode(TimerNodeKind.Unknown, null);
        }

        return segments switch
        {
            [] => new TimerNode(TimerNodeKind.Root, null),
            [var id] when !IsReserved(id) => new TimerNode(TimerNodeKind.TimerDir, id),
            [var id, TimerFileName] when !IsReserved(id) => new TimerNode(TimerNodeKind.TimerFile, id),
            [var id, StatusFileName] when !IsReserved(id) => new TimerNode(TimerNodeKind.StatusFile, id),
            _ => new TimerNode(TimerNodeKind.Unknown, null)
        };
    }

    private static bool IsReserved(string segment) =>
        segment is TimerFileName or StatusFileName;
}
```

Create `Domain/Tools/Timers/Vfs/TimerFileSystem.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Timers.Vfs;

// Hub-local countdown timers as a VFS: create /<id>/timer.json to arm, read status.json for time
// left, delete the directory to cancel. Timers are immutable (delete and recreate) and fire once.
public sealed class TimerFileSystem(ITimerStore store, TimeProvider timeProvider) : IFileSystemBackend
{
    public string FilesystemName => "timers";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _parseOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        var prefix = string.IsNullOrEmpty(basePath?.Trim('/')) ? string.Empty : basePath.Trim('/') + "/";

        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        var matches = GlobRegex.CompileMatcher(prefix + effectivePattern);

        var dirs = all.Select(t => t.Id).Where(matches).Select(id => $"/{id}/");
        if (dirsOnly)
        {
            return Glob(dirs.OrderBy(p => p, StringComparer.Ordinal).ToList());
        }

        var files = all.SelectMany(t => new[]
            {
                $"{t.Id}/{TimerPath.TimerFileName}",
                $"{t.Id}/{TimerPath.StatusFileName}"
            })
            .Where(matches)
            .Select(p => $"/{p}");
        return Glob(dirs.Concat(files).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        var exists = await NodeExistsAsync(node, ct);
        var isDir = node.Kind is TimerNodeKind.Root or TimerNodeKind.TimerDir;
        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = exists,
            Path = path,
            IsDirectory = exists ? isDir : null
        });
    }

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        string content;
        switch (node.Kind)
        {
            case TimerNodeKind.TimerFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderSpec(t);
                break;
            case TimerNodeKind.StatusFile when await GetTimerAsync(node, ct) is { } t:
                content = RenderStatus(t);
                break;
            default:
                return NotFound<FsReadResult>(path);
        }

        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    public async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        var matcher = new Regex(regex ? query : Regex.Escape(query), RegexOptions.IgnoreCase);
        var scope = path ?? directoryPath;

        var scoped = VfsContentSearch.MatchesFilePattern(filePattern, TimerPath.TimerFileName)
            ? await ScopeTimersAsync(scope, ct)
            : [];

        var results = new List<FsSearchFileResult>();
        var totalMatches = 0;
        var filesWithMatches = 0;
        var filesSearched = 0;
        var truncated = false;

        foreach (var timer in scoped)
        {
            if (totalMatches >= maxResults)
            {
                truncated = true;
                break;
            }
            filesSearched++;
            var lines = RenderSpec(timer).Split('\n');
            var (matches, more) = VfsContentSearch.FindMatches(lines, matcher, contextLines, maxResults - totalMatches);
            truncated |= more;
            if (matches.Count == 0)
            {
                continue;
            }
            filesWithMatches++;
            totalMatches += matches.Count;
            results.Add(VfsContentSearch.BuildFileResult($"/{timer.Id}/{TimerPath.TimerFileName}", matches, outputMode));
        }

        return new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = scope ?? "/",
            FilesSearched = filesSearched,
            FilesWithMatches = filesWithMatches,
            TotalMatches = totalMatches,
            Truncated = truncated,
            Results = results
        });
    }

    public async Task<FsResult<FsCreateResult>> CreateAsync(
        string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        if (node.Kind != TimerNodeKind.TimerFile || node.TimerId is null)
        {
            return Invalid<FsCreateResult>($"Create a timer at /<timerId>/{TimerPath.TimerFileName} (got '{path}')");
        }

        // Timers are immutable: create always rejects an existing id regardless of `overwrite`.
        if (await store.GetAsync(node.TimerId, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(
                Error(ToolError.Codes.AlreadyExists, $"Timer '{node.TimerId}' already exists"));
        }

        var spec = ParseSpec(content, out var specError);
        if (specError is not null)
        {
            return new FsResult<FsCreateResult>.Err(specError);
        }

        var validation = ValidateSpec(spec!);
        if (validation is not null)
        {
            return new FsResult<FsCreateResult>.Err(validation);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = node.TimerId,
            Text = spec!.Text,
            Target = spec.Target!,
            DurationSeconds = spec.DurationSeconds!.Value,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(spec.DurationSeconds.Value)
        }, ct);

        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = content.Length.ToString(), Lines = content.Split('\n').Length
        });
    }

    public async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        return node.Kind switch
        {
            TimerNodeKind.TimerFile when await GetTimerAsync(node, ct) is not null =>
                Unsupported<FsEditResult>("Timers are immutable — delete the timer and create a new one."),
            TimerNodeKind.StatusFile when await GetTimerAsync(node, ct) is not null =>
                ReadOnly<FsEditResult>(path),
            _ => NotFound<FsEditResult>(path)
        };
    }

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = TimerPath.Parse(path);
        if (node.Kind != TimerNodeKind.TimerDir)
        {
            return node.Kind is TimerNodeKind.TimerFile or TimerNodeKind.StatusFile
                   && await GetTimerAsync(node, ct) is not null
                ? Invalid<FsRemoveResult>($"Cancel the timer by deleting its directory: /{node.TimerId}")
                : NotFound<FsRemoveResult>(path);
        }

        return await store.CancelAsync(node.TimerId!, ct)
            ? new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "deleted", Message = "cancelled", OriginalPath = path, TrashPath = ""
            })
            : NotFound<FsRemoveResult>(path);
    }

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsMoveResult>("The timers filesystem does not support move."));

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsExecResult>("The timers filesystem does not support exec."));

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCopyResult>("The timers filesystem does not support copy."));

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException("The timers filesystem does not support raw byte streaming.");

    public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        throw new NotSupportedException("The timers filesystem does not support raw byte streaming.");

    private sealed record SpecDto
    {
        public int? DurationSeconds { get; init; }
        public string? Text { get; init; }
        public AnnounceTarget? Target { get; init; }
    }

    private static SpecDto? ParseSpec(string content, out ToolErrorResult? error)
    {
        error = null;
        try
        {
            var spec = JsonSerializer.Deserialize<SpecDto>(content, _parseOptions);
            if (spec is null)
            {
                error = Error(ToolError.Codes.InvalidArgument, "timer.json is empty");
            }
            return spec;
        }
        catch (JsonException ex)
        {
            error = Error(ToolError.Codes.InvalidArgument, $"Invalid timer.json: {ex.Message}");
            return null;
        }
    }

    private static ToolErrorResult? ValidateSpec(SpecDto spec)
    {
        if (spec.DurationSeconds is not > 0)
        {
            return Error(ToolError.Codes.InvalidArgument, "durationSeconds must be a positive integer");
        }

        var target = spec.Target;
        var hasTarget = target is not null
            && (target.SatelliteId is not null
                || target.SatelliteIds is { Count: > 0 }
                || target.Room is not null
                || target.All == true);
        return hasTarget
            ? null
            : Error(ToolError.Codes.InvalidArgument,
                "target is required: {satelliteId | satelliteIds | room | all}");
    }

    private string RenderSpec(ArmedTimer t) => JsonSerializer.Serialize(new
    {
        durationSeconds = t.DurationSeconds,
        text = t.Text,
        target = t.Target
    }, _json);

    private string RenderStatus(ArmedTimer t)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return JsonSerializer.Serialize(new
        {
            remainingSeconds = Math.Max(0, (int)Math.Ceiling((t.FiresAtUtc - now).TotalSeconds)),
            firesAt = ToZone(t.FiresAtUtc)
        }, _json);
    }

    // Stored times are UTC; render them in the operating zone so the LLM reads local wall-clock.
    private DateTimeOffset ToZone(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), timeProvider.LocalTimeZone);

    private async Task<ArmedTimer?> GetTimerAsync(TimerNode node, CancellationToken ct) =>
        node.TimerId is null ? null : await store.GetAsync(node.TimerId, ct);

    private async Task<bool> NodeExistsAsync(TimerNode node, CancellationToken ct) => node.Kind switch
    {
        TimerNodeKind.Root => true,
        TimerNodeKind.TimerDir or TimerNodeKind.TimerFile or TimerNodeKind.StatusFile =>
            await GetTimerAsync(node, ct) is not null,
        _ => false
    };

    private async Task<IReadOnlyList<ArmedTimer>> ScopeTimersAsync(string? scope, CancellationToken ct)
    {
        var all = await store.ListAsync(ct);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return all;
        }

        var node = TimerPath.Parse(scope);
        return node.Kind switch
        {
            TimerNodeKind.Root => all,
            TimerNodeKind.TimerDir or TimerNodeKind.TimerFile or TimerNodeKind.StatusFile =>
                all.Where(t => t.Id == node.TimerId).ToList(),
            _ => []
        };
    }

    private static FsResult<FsGlobResult> Glob(IReadOnlyList<string> entries) =>
        new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = entries,
            Truncated = false,
            Total = entries.Count
        });

    private static FsResult<T> ReadOnly<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));

    private static FsResult<T> Invalid<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.InvalidArgument, message));

    private static FsResult<T> NotFound<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));

    private static FsResult<T> Unsupported<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, message));

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };
}
```

(Adjust `using Domain.Tools;` / helper namespaces to match `ScheduleFileSystem.cs`'s usings exactly — `ToolError`, `ToolErrorResult`, `GlobRegex`, `VfsContentSearch` all resolve the same way there.)

- [ ] **Step 8: Run journey tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TimerFileSystemJourneyTests|FullyQualifiedName~InMemoryTimerStoreTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Domain/DTOs/Voice/ArmedTimer.cs Domain/Contracts/ITimerStore.cs Domain/Tools/Timers/ Infrastructure/Timers/ Tests/Unit/Domain/Timers/ Tests/Unit/Infrastructure/InMemoryTimerStoreTests.cs
git commit -m "feat(timers): /timers VFS engine with in-memory store"
```

---

### Task 7: `TimerFireService` — due timers ring as insistent timer alerts

**Files:**
- Create: `McpChannelVoice/Services/IInsistentAnnouncer.cs`, `McpChannelVoice/Services/TimerFireService.cs`
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs` (implement the interface)
- Test: `Tests/Unit/McpChannelVoice/TimerFireServiceTests.cs` (new)

**Interfaces:**
- Consumes: `ITimerStore.TakeDueAsync` (Task 6), `AnnounceKind.Timer` (Task 1).
- Produces: `IInsistentAnnouncer { Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct); }` implemented by `InsistentAnnouncementController`; `TimerFireService(ITimerStore store, IInsistentAnnouncer announcer, TimeProvider time, ILogger<TimerFireService> logger) : BackgroundService` polling at 1 s; fired requests use `Kind = Timer`, `Text = timer.Text ?? $"{timer.Id} timer"`, `Insistent = { GapSeconds = 10, MaxRepeats = 12 }`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/TimerFireServiceTests.cs`:

```csharp
using Domain.DTOs.Voice;
using Infrastructure.Timers;
using McpChannelVoice.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TimerFireServiceTests
{
    private sealed class RecordingAnnouncer : IInsistentAnnouncer
    {
        private readonly List<AnnounceRequest> _requests = [];
        public IReadOnlyList<AnnounceRequest> Requests
        {
            get { lock (_requests) { return _requests.ToList(); } }
        }
        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            lock (_requests) { _requests.Add(request); }
            return Task.FromResult(new AnnounceResponse { AnnouncementId = "a1", Satellites = [] });
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            { throw new TimeoutException("condition not met"); }
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DueTimer_RingsAsInsistentTimerAlert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var announcer = new RecordingAnnouncer();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "pasta",
            Text = "pasta is ready",
            Target = new AnnounceTarget { Room = "Kitchen" },
            DurationSeconds = 5,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(5)
        });
        var service = new TimerFireService(store, announcer, time, NullLogger<TimerFireService>.Instance);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(50);
        announcer.Requests.ShouldBeEmpty(); // not due yet

        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Requests.Count == 1, TimeSpan.FromSeconds(5));

        var request = announcer.Requests[0];
        request.Kind.ShouldBe(AnnounceKind.Timer);
        request.Text.ShouldBe("pasta is ready");
        request.Target.Room.ShouldBe("Kitchen");
        request.Insistent.ShouldNotBeNull();
        request.Insistent!.GapSeconds.ShouldBe(10);
        request.Insistent.MaxRepeats.ShouldBe(12);
        (await store.GetAsync("pasta")).ShouldBeNull(); // fire-once

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_TimerWithoutText_SpeaksIdAsName()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var announcer = new RecordingAnnouncer();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "pasta",
            Target = new AnnounceTarget { Room = "Kitchen" },
            DurationSeconds = 1,
            CreatedAtUtc = now,
            FiresAtUtc = now.AddSeconds(1)
        });
        var service = new TimerFireService(store, announcer, time, NullLogger<TimerFireService>.Instance);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Requests.Count == 1, TimeSpan.FromSeconds(5));

        announcer.Requests[0].Text.ShouldBe("pasta timer");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_AnnouncerThrows_LoopSurvives()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryTimerStore();
        var announcer = new ThrowingThenRecordingAnnouncer();
        var now = time.GetUtcNow().UtcDateTime;
        await store.ArmAsync(new ArmedTimer
        {
            Id = "first", Target = new AnnounceTarget { Room = "Ghost" }, DurationSeconds = 1,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(1)
        });
        await store.ArmAsync(new ArmedTimer
        {
            Id = "second", Target = new AnnounceTarget { Room = "Kitchen" }, DurationSeconds = 3,
            CreatedAtUtc = now, FiresAtUtc = now.AddSeconds(3)
        });
        var service = new TimerFireService(store, announcer, time, NullLogger<TimerFireService>.Instance);

        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Calls >= 1, TimeSpan.FromSeconds(5)); // first fires, throws
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => announcer.Succeeded.Count == 1, TimeSpan.FromSeconds(5)); // loop survived

        announcer.Succeeded[0].ShouldBe("second timer");
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class ThrowingThenRecordingAnnouncer : IInsistentAnnouncer
    {
        private int _calls;
        private readonly List<string> _succeeded = [];
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyList<string> Succeeded
        {
            get { lock (_succeeded) { return _succeeded.ToList(); } }
        }
        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (request.Target.Room == "Ghost")
            {
                throw new AnnounceTargetNotFoundException("no such room");
            }
            lock (_succeeded) { _succeeded.Add(request.Text); }
            return Task.FromResult(new AnnounceResponse { AnnouncementId = "a1", Satellites = [] });
        }
    }
}
```

(`AnnounceTargetNotFoundException` lives in `McpChannelVoice.Services` — check its namespace via the controller's usings and import accordingly.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TimerFireServiceTests"`
Expected: FAIL to compile (`IInsistentAnnouncer`, `TimerFireService` not defined).

- [ ] **Step 3: Implement**

Create `McpChannelVoice/Services/IInsistentAnnouncer.cs`:

```csharp
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Test seam over InsistentAnnouncementController for in-process callers (TimerFireService).
public interface IInsistentAnnouncer
{
    Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct);
}
```

In `McpChannelVoice/Services/InsistentAnnouncementController.cs` change the class declaration to implement it:

```csharp
public sealed class InsistentAnnouncementController(
    ...same primary constructor...) : IInsistentAnnouncer
```

Create `McpChannelVoice/Services/TimerFireService.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Polls the timer store once per second and rings each due timer as an insistent timer alert —
// no HTTP hop, no LLM in the fire path. Timers use a fixed tighter cadence than alarms.
public sealed class TimerFireService(
    ITimerStore store,
    IInsistentAnnouncer announcer,
    TimeProvider time,
    ILogger<TimerFireService> logger) : BackgroundService
{
    private static readonly InsistentOptions _timerRing = new() { GapSeconds = 10, MaxRepeats = 12 };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var due = await store.TakeDueAsync(time.GetUtcNow().UtcDateTime, ct);
                foreach (var armed in due)
                {
                    await FireAsync(armed, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    private async Task FireAsync(ArmedTimer armed, CancellationToken ct)
    {
        try
        {
            await announcer.StartAsync(new AnnounceRequest
            {
                Target = armed.Target,
                Text = armed.Text ?? $"{armed.Id} timer",
                Kind = AnnounceKind.Timer,
                Insistent = _timerRing
            }, ct);
            logger.LogInformation("Timer {TimerId} fired", armed.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The timer was already removed by TakeDueAsync; a bad target means it just doesn't ring
            // (documented v1 behavior — no durability/retry).
            logger.LogWarning(ex, "Timer {TimerId} failed to ring", armed.Id);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TimerFireServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/IInsistentAnnouncer.cs McpChannelVoice/Services/TimerFireService.cs McpChannelVoice/Services/InsistentAnnouncementController.cs Tests/Unit/McpChannelVoice/TimerFireServiceTests.cs
git commit -m "feat(voice): TimerFireService rings due timers as insistent alerts"
```

---

### Task 8: MCP surface for `/timers` — fs tools, resource, prompt, wiring

Make the voice server serve `filesystem://timers` on its existing `/mcp` endpoint. The `nabu` agent already lists that endpoint in `mcpServerEndpoints` and `ThreadSession` already strips channel-protocol tools from dual-role servers, so **no agent or compose config changes are needed**.

**Files:**
- Create: `McpChannelVoice/McpTools/FsGlobTool.cs`, `FsInfoTool.cs`, `FsReadTool.cs`, `FsSearchTool.cs`, `FsCreateTool.cs`, `FsEditTool.cs`, `FsDeleteTool.cs`, `FsMoveTool.cs`, `FsExecTool.cs`
- Create: `McpChannelVoice/McpResources/FileSystemResource.cs`
- Create: `Domain/Prompts/TimerPrompt.cs`, `McpChannelVoice/McpPrompts/TimersSystemPrompt.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `CLAUDE.md` (voice row: mention `/timers`)
- Test: `Tests/Unit/Domain/Prompts/VfsPromptToolNameConsistencyTests.cs`, `Tests/Unit/Domain/Prompts/TimerPromptTests.cs` (new)

**Interfaces:**
- Consumes: `TimerFileSystem` (Task 6), `InMemoryTimerStore` (Task 6), `TimerFireService`/`IInsistentAnnouncer` (Task 7).
- Produces: MCP tools named `fs_glob`/`fs_info`/`fs_read`/`fs_search`/`fs_create`/`fs_edit`/`fs_delete`/`fs_move`/`fs_exec` (raw names — the agent's domain VFS tools call them; they are filtered from the LLM); resource `filesystem://timers` (mount `/timers`); MCP prompt `timers_prompt` (`TimerPrompt.Name`).

- [ ] **Step 1: Write the failing prompt tests**

Create `Tests/Unit/Domain/Prompts/TimerPromptTests.cs`:

```csharp
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class TimerPromptTests
{
    [Fact]
    public void Prompt_TeachesTheTimersIdiom()
    {
        TimerPrompt.Prompt.ShouldContain("/timers");
        TimerPrompt.Prompt.ShouldContain("durationSeconds");
        TimerPrompt.Prompt.ShouldContain("status.json");
        TimerPrompt.Prompt.ShouldContain("speaking room");
        TimerPrompt.Prompt.ShouldContain("calendar"); // steers alarms back to the HA calendar
    }
}
```

In `Tests/Unit/Domain/Prompts/VfsPromptToolNameConsistencyTests.cs` add the new prompt to the `VfsPrompts` member data:

```csharp
        ["timers_prompt", TimerPrompt.Prompt],
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TimerPromptTests|FullyQualifiedName~VfsPromptToolNameConsistencyTests"`
Expected: FAIL to compile (`TimerPrompt` not defined).

- [ ] **Step 3: Implement the prompt**

Create `Domain/Prompts/TimerPrompt.cs` (tool names via the `Vfs*Tool.Name` constants — see `SchedulingPrompt` for the interpolation pattern and the exact constant classes in `Domain/Tools/FileSystem/`):

```csharp
using Domain.Tools.FileSystem;

namespace Domain.Prompts;

public static class TimerPrompt
{
    public const string Name = "timers_prompt";
    public const string Description =
        "Explains how to manage short countdown timers via the /timers filesystem";

    public static readonly string Prompt = $"""
        ## Timers

        Short countdowns ("set a timer for 5 minutes", "pasta timer for 8 minutes") live in the
        virtual filesystem at `/timers` — NOT the Home Assistant alarms calendar (that is for
        clock-time alarms and reminders) and NOT `/schedules` (agent tasks). When a timer expires
        it rings insistently (tone + spoken message) on the target satellites until the user says
        the wake word there, presses the button, or a repeat cap is reached.

        - Create: `{VfsTextCreateTool.Name}` at `/timers/<descriptive-id>/timer.json` with JSON
          `{{"durationSeconds": <int>, "text"?: "<spoken message>", "target": {{...}}}}`.
          `target` is `{{satelliteId | satelliteIds | room | all}}` — default to the **speaking
          room** (the room this request came from) unless another room is named. When `text` is
          omitted the timer announces itself as "<id> timer", so pick a descriptive id (e.g.
          `pasta`).
        - Time left: `{VfsTextReadTool.Name}` on `/timers/<id>/status.json` → `remainingSeconds`
          and `firesAt`.
        - List: `{VfsGlobFilesTool.Name}` on `/timers`.
        - Cancel: `{VfsRemoveTool.Name}` on `/timers/<id>`.
        - Timers are immutable and fire once — to change one, delete it and create a new one. To
          extend a timer the user just dismissed ("two more minutes"), create a new timer with the
          remaining request.
        """;
}
```

(Verify the exact `Vfs*Tool` class names/`Name` constants in `Domain/Tools/FileSystem/` — `VfsTextCreateTool`, `VfsTextReadTool`, `VfsGlobFilesTool`, `VfsRemoveTool` per the CLAUDE.md tool list — and adjust if any differ.)

- [ ] **Step 4: Run prompt tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TimerPromptTests|FullyQualifiedName~VfsPromptToolNameConsistencyTests"`
Expected: PASS (consistency test proves no `fs_*` names leak into the prompt).

- [ ] **Step 5: Add the MCP tools, resource, prompt class, and wiring**

Create the nine tool wrappers in `McpChannelVoice/McpTools/` — each mirrors the scheduling server's pattern (`McpServerScheduling/McpTools/FsReadTool.cs`), delegating to `TimerFileSystem`. All nine:

```csharp
using System.ComponentModel;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public class FsGlobTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("List timer filesystem entries matching a glob pattern")]
    public async Task<CallToolResult> McpRun(string basePath, string pattern, CancellationToken ct = default)
        => ToolResponse.Create(await fs.GlobAsync(basePath, pattern, ct));
}
```

```csharp
[McpServerToolType]
public class FsInfoTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_info")]
    [Description("Existence/kind info for a timer filesystem path")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.InfoAsync(path, ct));
}
```

```csharp
[McpServerToolType]
public class FsReadTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a timer filesystem file (timer.json/status.json)")]
    public async Task<CallToolResult> McpRun(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ReadAsync(path, offset, limit, ct));
}
```

```csharp
[McpServerToolType]
public class FsSearchTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Search timer specs by content")]
    public async Task<CallToolResult> McpRun(
        string query, bool regex = false, string? path = null, string? directoryPath = null,
        string? filePattern = null, int maxResults = 100, int contextLines = 0,
        VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content, CancellationToken ct = default)
        => ToolResponse.Create(await fs.SearchAsync(
            query, regex, path, directoryPath, filePattern, maxResults, contextLines, outputMode, ct));
}
```

```csharp
[McpServerToolType]
public class FsCreateTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_create")]
    [Description("Arm a timer by creating /<timerId>/timer.json ({durationSeconds, text?, target})")]
    public async Task<CallToolResult> McpRun(
        string path, string content, bool overwrite = false, bool createDirectories = false, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CreateAsync(path, content, overwrite, createDirectories, ct));
}
```

```csharp
[McpServerToolType]
public class FsEditTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Unsupported on timers (immutable) — kept for VFS surface completeness")]
    public async Task<CallToolResult> McpRun(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default)
        => ToolResponse.Create(await fs.EditAsync(path, edits, ct));
}
```

```csharp
[McpServerToolType]
public class FsDeleteTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Cancel a timer by deleting its directory /<timerId>")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.DeleteAsync(path, ct));
}
```

```csharp
[McpServerToolType]
public class FsMoveTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_move")]
    [Description("Unsupported on timers — kept for VFS surface completeness")]
    public async Task<CallToolResult> McpRun(string sourcePath, string destinationPath, CancellationToken ct = default)
        => ToolResponse.Create(await fs.MoveAsync(sourcePath, destinationPath, ct));
}
```

```csharp
[McpServerToolType]
public class FsExecTool(TimerFileSystem fs)
{
    [McpServerTool(Name = "fs_exec")]
    [Description("Unsupported on timers — kept for VFS surface completeness")]
    public async Task<CallToolResult> McpRun(string path, string command, int? timeoutSeconds = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ExecAsync(path, command, timeoutSeconds, ct));
}
```

(Match each wrapper's parameter list to the scheduling server's corresponding tool file — copy the signature, swap `ScheduleFileSystem` → `TimerFileSystem`, reword the description. `TextEdit`/`VfsTextSearchOutputMode` usings come from the same namespaces the scheduling tools import.)

Create `McpChannelVoice/McpResources/FileSystemResource.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(UriTemplate = "filesystem://timers", Name = "Timers Filesystem", MimeType = "application/json")]
    [Description("Voice countdown-timer control surface")]
    public string GetInfo() => JsonSerializer.Serialize(new
    {
        name = "timers",
        mountPoint = "/timers",
        description = "Short countdown timers that ring on the voice satellites. Arm one by creating /timers/<descriptive-id>/timer.json with JSON {durationSeconds, text?, target} — target is {satelliteId | satelliteIds | room | all}; default it to the speaking room. Read /timers/<id>/status.json for remainingSeconds/firesAt; cancel by deleting /timers/<id>. Timers are immutable (delete and recreate) and fire once, ringing tone + message until dismissed by wake word/button or capped. Use the HA alarms calendar for clock-time alarms/reminders, not timers."
    });
}
```

Create `McpChannelVoice/McpPrompts/TimersSystemPrompt.cs`:

```csharp
using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpPrompts;

[McpServerPromptType]
public class TimersSystemPrompt
{
    [McpServerPrompt(Name = TimerPrompt.Name)]
    [Description(TimerPrompt.Description)]
    public string GetTimerPrompt() => TimerPrompt.Prompt;
}
```

In `McpChannelVoice/Modules/ConfigModule.cs`:

1. Add DI registrations next to the announce services:

```csharp
        services.AddSingleton<Domain.Contracts.ITimerStore, Infrastructure.Timers.InMemoryTimerStore>();
        services.AddSingleton(sp => new Domain.Tools.Timers.Vfs.TimerFileSystem(
            sp.GetRequiredService<Domain.Contracts.ITimerStore>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IInsistentAnnouncer>(sp => sp.GetRequiredService<InsistentAnnouncementController>());
        services.AddHostedService<TimerFireService>();
```

2. Extend the MCP server chain after the existing `.WithTools<CreateConversationTool>()`:

```csharp
            .WithTools<FsGlobTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsReadTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsExecTool>()
            .WithResources<McpResources.FileSystemResource>()
            .WithPrompts<VoiceSystemPrompt>()
            .WithPrompts<TimersSystemPrompt>()
```

(keeping the existing `.WithPrompts<VoiceSystemPrompt>()` — just add the new prompt registration next to it).

In `CLAUDE.md`, `McpChannelVoice` row: append "; dual-role: exposes `filesystem://timers` (hub-local countdown timers that ring insistently)" to the purpose text.

- [ ] **Step 6: Build and run the voice test suite**

Run: `dotnet build agent.sln && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"`
Expected: build succeeds; all voice tests PASS.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/McpTools/Fs*.cs McpChannelVoice/McpResources/ McpChannelVoice/McpPrompts/TimersSystemPrompt.cs McpChannelVoice/Modules/ConfigModule.cs Domain/Prompts/TimerPrompt.cs Tests/Unit/Domain/Prompts/TimerPromptTests.cs Tests/Unit/Domain/Prompts/VfsPromptToolNameConsistencyTests.cs CLAUDE.md
git commit -m "feat(voice): expose filesystem://timers on the dual-role voice server"
```

---

### Task 9: Ack-gated escalation webhook

When an **alarm** (never a timer) caps out unacknowledged, POST `{text, satellites, rounds}` to a configured HA webhook. Fire-and-forget; unset URL = feature off.

**Files:**
- Modify: `McpChannelVoice/Settings/AnnounceSettings.cs` (+`EscalationSettings`)
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs` (+`IHttpClientFactory`, escalation call)
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (`services.AddHttpClient();`)
- Modify: `McpChannelVoice/appsettings.json` (`Announce.Escalation.WebhookUrl` placeholder)
- Modify: `DockerCompose/docker-compose.yml` (`mcp-channel-voice` env: `Announce__Escalation__WebhookUrl`)
- Modify: `docs/home-assistant-alarms.md` (webhook automation example)
- Test: `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`; fix controller construction in `Tests/Unit/McpChannelVoice/AnnounceEndpointAuthTests.cs`, `Tests/Integration/McpChannelVoice/AnnounceEndToEndTests.cs`, `Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs`

**Interfaces:**
- Consumes: `AnnounceKind` (Task 1).
- Produces: `AnnounceSettings.Escalation` (`EscalationSettings { string? WebhookUrl }`); `InsistentAnnouncementController` primary constructor gains `IHttpClientFactory httpClientFactory` (last parameter before the logger).

- [ ] **Step 1: Write the failing tests**

In `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`:

1. Add the HTTP fakes inside the test class:

```csharp
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Uri? Uri, string Body)> _requests = [];
        public IReadOnlyList<(Uri? Uri, string Body)> Requests
        {
            get { lock (_requests) { return _requests.ToList(); } }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (_requests) { _requests.Add((request.RequestUri, body)); }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
```

2. Extend `BuildHarness` with optional settings + handler, and pass the factory into the controller:

```csharp
    private static Harness BuildHarness(
        FakeTimeProvider time, bool online, VoiceSettings? settings = null,
        RecordingHandler? http = null, params string[] satelliteIds)
```

(the controller construction becomes)

```csharp
        var controller = new InsistentAnnouncementController(
            registry, sessions, tts.Object, settings ?? new VoiceSettings(), alerts, publisher, time,
            new StubHttpClientFactory(http ?? new RecordingHandler()),
            NullLogger<InsistentAnnouncementController>.Instance);
```

Update the existing `BuildHarness(time, online: true, "kitchen-01")` call sites to `BuildHarness(time, online: true, satelliteIds: "kitchen-01")` (named param keeps them compiling past the new optional parameters).

3. Add the behavior tests:

```csharp
    private static VoiceSettings SettingsWithEscalation() => new()
    {
        Announce = new AnnounceSettings
        {
            Escalation = new EscalationSettings { WebhookUrl = "http://ha:8123/api/webhook/alarm-unacked" }
        }
    };

    [Fact]
    public async Task Unacknowledged_Alarm_PostsEscalationWebhook()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: true, SettingsWithEscalation(), http, "kitchen-01");
        var (pump, plays) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "Take out the trash",
                Insistent = new() { GapSeconds = 30, MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => http.Requests.Count == 1, TimeSpan.FromSeconds(5));

        http.Requests[0].Uri!.ToString().ShouldBe("http://ha:8123/api/webhook/alarm-unacked");
        http.Requests[0].Body.ShouldContain("Take out the trash");
        http.Requests[0].Body.ShouldContain("kitchen-01");
        http.Requests[0].Body.ShouldContain("\"rounds\":1");

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }

    [Fact]
    public async Task Unacknowledged_Timer_DoesNotEscalate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: true, SettingsWithEscalation(), http, "kitchen-01");
        var (pump, plays) = PumpPlays(h.Sessions.Get("kitchen-01")!, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "pasta",
                Kind = AnnounceKind.Timer,
                Insistent = new() { GapSeconds = 30, MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmUnacknowledged),
            TimeSpan.FromSeconds(5));
        http.Requests.ShouldBeEmpty();

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }

    [Fact]
    public async Task Acknowledged_Alarm_DoesNotEscalate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var http = new RecordingHandler();
        var h = BuildHarness(time, online: true, SettingsWithEscalation(), http, "kitchen-01");
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

        h.Alerts.Acknowledge("kitchen-01").ShouldNotBeEmpty();
        await WaitUntilAsync(
            () => h.Publisher.Events.Any(e => e.Metric == VoiceMetric.AlarmAcknowledged),
            TimeSpan.FromSeconds(5));

        http.Requests.ShouldBeEmpty();

        h.Sessions.Get("kitchen-01")!.CompletePlayback();
        await pump;
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnouncementControllerTests"`
Expected: FAIL to compile (`EscalationSettings`, controller ctor arity).

- [ ] **Step 3: Implement**

`McpChannelVoice/Settings/AnnounceSettings.cs`:

```csharp
public record AnnounceSettings
{
    public bool Enabled { get; init; }
    public string Token { get; init; } = "";
    public bool BindToLoopbackOnly { get; init; }
    public int QueueMaxDepth { get; init; } = 8;
    public int MaxTextLength { get; init; } = 50000;
    public InsistentDefaults Insistent { get; init; } = new();
    public EscalationSettings Escalation { get; init; } = new();
}

public record EscalationSettings
{
    // HA webhook POSTed when an ALARM caps out unacknowledged (timers never escalate).
    // Null/empty disables escalation.
    public string? WebhookUrl { get; init; }
}
```

`McpChannelVoice/Services/InsistentAnnouncementController.cs`:

1. Primary constructor gains `IHttpClientFactory httpClientFactory,` before the logger parameter.
2. In `RunLoopAsync`'s unacknowledged branch:

```csharp
            else
            {
                await SafePublishAsync(AlarmEvent(VoiceMetric.AlarmUnacknowledged, targetIds, round));
                await TryEscalateAsync(request, targetIds, round);
            }
```

3. Add the helper:

```csharp
    // Ack-gated escalation: an unacknowledged ALARM (never a timer) is handed to HA via webhook so an
    // automation can notify another channel. Fire-and-forget: failures are logged, never retried.
    private async Task TryEscalateAsync(AnnounceRequest request, IReadOnlyList<string> targetIds, int rounds)
    {
        var url = settings.Announce.Escalation.WebhookUrl;
        if (request.Kind != AnnounceKind.Alarm || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(InsistentAnnouncementController));
            using var response = await client.PostAsJsonAsync(
                url, new { text = request.Text, satellites = targetIds, rounds });
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Alarm escalation webhook returned {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Alarm escalation webhook failed");
        }
    }
```

(`PostAsJsonAsync` needs `using System.Net.Http.Json;`.)

`McpChannelVoice/Modules/ConfigModule.cs` — add `services.AddHttpClient();` next to the announce registrations.

Fix the controller construction/DI in the three test files that build it directly or via a service collection (`AnnounceEndpointAuthTests`, `AnnounceEndToEndTests`, `InsistentAnnounceE2ETests`): where DI is used add `services.AddHttpClient();`; where constructed by hand pass a `StubHttpClientFactory` (copy the fake, or extract both fakes to a shared `Tests/Unit/McpChannelVoice/HttpFakes.cs` if reused).

`McpChannelVoice/appsettings.json` — inside `"Announce"` add:

```json
        "Escalation": {
            "WebhookUrl": ""
        }
```

`DockerCompose/docker-compose.yml` — in the `mcp-channel-voice` service `environment` block add (matching the file's existing env style):

```yaml
      Announce__Escalation__WebhookUrl: ""
```

`docs/home-assistant-alarms.md` — replace the "Conditional escalate…is not built in" limitation bullet with a new setup step:

```markdown
5. **Ack-gated escalation (optional).** Set `Announce__Escalation__WebhookUrl` on the
   `mcp-channel-voice` container to an HA webhook, e.g.
   `http://homeassistant:8123/api/webhook/alarm-unacked`. When an alarm reaches its repeat cap
   with no acknowledgment the hub POSTs `{"text", "satellites", "rounds"}` there. Bridge it in HA:

       alias: Alarm unacknowledged escalation
       trigger:
         - platform: webhook
           webhook_id: alarm-unacked
           local_only: true
       action:
         - service: notify.mobile_app_phone
           data:
             message: "Unacknowledged alarm: {{ trigger.json.text }}"

   Timers never escalate. This replaces the old advice to fire a parallel notify at trigger time.
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build agent.sln && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnouncementControllerTests|FullyQualifiedName~AnnounceEndpointAuthTests"`
Expected: PASS. Also run the two integration suites if Docker redis is up: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnounceE2ETests|FullyQualifiedName~AnnounceEndToEndTests"`.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Settings/AnnounceSettings.cs McpChannelVoice/Services/InsistentAnnouncementController.cs McpChannelVoice/Modules/ConfigModule.cs McpChannelVoice/appsettings.json DockerCompose/docker-compose.yml docs/home-assistant-alarms.md Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs Tests/Unit/McpChannelVoice/AnnounceEndpointAuthTests.cs Tests/Integration/McpChannelVoice/AnnounceEndToEndTests.cs Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs
git commit -m "feat(voice): ack-gated escalation webhook for unacknowledged alarms"
```

---

### Task 10: Timer end-to-end integration test (arm via VFS → fire → ring → wake dismiss)

**Files:**
- Test: `Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs` (add a test; reuse the file's existing `WaitForAsync`/`FakeTtsAudio`/`GetFreePort` helpers)

**Interfaces:**
- Consumes: everything from Tasks 1–9 (`TimerFileSystem`, `InMemoryTimerStore`, `TimerFireService`, `IInsistentAnnouncer`, `ActiveAlertRegistry.Acknowledge` returning `DismissedAlert`s).

- [ ] **Step 1: Write the test** (it exercises already-implemented behavior, so it should pass immediately — its value is pinning the full wiring)

Add to `Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs` (new usings: `Domain.Tools.Timers.Vfs`, `Domain.DTOs.FileSystem`, `Infrastructure.Timers`):

```csharp
    [Fact]
    public async Task VfsArmedTimer_Fires_RingsAndWakeDismisses()
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

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, GetFreePort()));
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
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<InsistentAnnouncementController>();
        builder.Services.AddSingleton<IInsistentAnnouncer>(sp => sp.GetRequiredService<InsistentAnnouncementController>());
        builder.Services.AddSingleton<ITimerStore, InMemoryTimerStore>();
        builder.Services.AddHostedService<TimerFireService>();
        builder.Services.AddHostedService<WyomingSatelliteHost>();

        var app = builder.Build();
        await app.StartAsync(ct);

        var sessions = app.Services.GetRequiredService<SatelliteSessionRegistry>();
        await WaitForAsync(() => sessions.Get("kitchen-01") is not null, TimeSpan.FromSeconds(5));

        // Arm through the VFS surface — the same path the agent's fs tools hit.
        var fs = new TimerFileSystem(app.Services.GetRequiredService<ITimerStore>(), TimeProvider.System);
        var created = await fs.CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "text": "pasta is ready", "target": {"room": "Kitchen"}}""",
            false, true, ct);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        // Fires within duration + 1s poll (+ slack) and rings on the satellite.
        await WaitForAsync(() => !audioStarts.IsEmpty, TimeSpan.FromSeconds(10));

        // Wake on the satellite dismisses it, reporting what was dismissed for snooze context.
        var dismissed = app.Services.GetRequiredService<ActiveAlertRegistry>().Acknowledge("kitchen-01");
        dismissed.ShouldHaveSingleItem();
        dismissed[0].Text.ShouldBe("pasta is ready");
        dismissed[0].Kind.ShouldBe(AnnounceKind.Timer);

        await app.StopAsync(CancellationToken.None);
        satListener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation */ }
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnounceE2ETests"`
Expected: PASS (both the existing alarm test and the new timer test). If the new test fails, that is a real wiring bug in Tasks 6–8 — debug it, don't weaken the test.

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/McpChannelVoice/InsistentAnnounceE2ETests.cs
git commit -m "test(voice): timer VFS-arm -> fire -> ring -> wake-dismiss integration test"
```

---

### Task 11: Full-suite verification sweep

**Files:** none new — verification only.

- [ ] **Step 1: Build everything**

Run: `dotnet build agent.sln`
Expected: success, no new warnings beyond the pre-existing baseline.

- [ ] **Step 2: Run the full unit suite**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`
Expected: PASS (judge any failure by type: pre-existing known failures — e.g. the McpAgent cleanup test — are not regressions; anything touching voice/timers/scheduling/chat-client must be green).

- [ ] **Step 3: Run the touched integration suites** (needs Docker redis for some)

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Integration.McpChannelVoice"`
Expected: PASS.

- [ ] **Step 4: Fix anything found, then final commit if fixes were needed**

```bash
git add -A && git commit -m "test: full-suite verification fixes for alarm improvements"
```

(Skip the commit if the tree is clean.)
