# Voice Conversation Mode — Wake-Free Follow-Up Turns — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After the agent speaks a voice reply, re-open the satellite mic for a few seconds so the user can speak a follow-up without the wake word; chain into a back-and-forth that falls back to wake-required mode on silence, error, or a turn cap.

**Architecture:** Hub-only. `wyoming-satellite` (WakeStreamingSatellite) streams from wake until it receives a `transcript`; we **withhold** that transcript so the satellite keeps the mic open, and segment one held-open stream server-side into successive utterances. Mic routing lives on `SatelliteSession` (a single active `UtteranceCapture`, `null` = discard → echo suppression). A per-connection `FollowUpConversation` coordinator runs the turn state machine via injected delegates; the `WyomingSatelliteHost` read loop becomes a pure frame demux. The closing `transcript` is sent only to end the conversation.

**Tech Stack:** .NET 10, C#, xUnit + Shouldly + Moq, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, Wyoming protocol over TCP.

**Spec:** `docs/superpowers/specs/2026-06-06-voice-conversation-followup-design.md`

**Key facts (verified against `rhasspy/wyoming-satellite`):** WakeStreamingSatellite streams until `Transcript`/`Error` with no internal timeout/VAD; it plays TTS while still streaming; on `Transcript` it stops, plays its "done" sound, and re-arms wake. The satellite ignores the `transcript` text — it is a control signal only.

**Conventions (from repo rules):** file-scoped namespaces; primary constructors; `record` DTOs; `TimeProvider` for time; prefer LINQ; **no XML doc comments**; **no trailing newline in any `.cs` file**; `{Method}_{Scenario}_{ExpectedResult}` test names; Shouldly assertions. The pre-commit hook runs `dotnet format` and re-stages whole files, so keep the working tree matching each commit.

**Build/test commands:**
- Build: `dotnet build Agent.sln`
- Run one test: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<TestClass>.<TestMethod>"`
- Run a class: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<TestClass>"`
- Non-E2E suite: `dotnet test Tests/Tests.csproj --filter "Category!=E2E"`

> NOTE: ~148 non-E2E tests fail pre-existing in this WSL env (DockerUnavailableException) — that is the baseline, not a regression. Judge each task by the tests it adds/touches.

---

## File Structure

**New files:**
- `McpChannelVoice/Settings/FollowUpSettings.cs` — follow-up config record.
- `McpChannelVoice/Services/ListeningChime.cs` — generated PCM earcon.
- `McpChannelVoice/Services/UtteranceCapture.cs` — one mic-capture sink (gate + buffered audio + outcome).
- `McpChannelVoice/Services/FollowUpConversation.cs` — per-connection turn state machine (the coordinator).
- Tests: `Tests/Unit/McpChannelVoice/ListeningChimeTests.cs`, `UtteranceCaptureTests.cs`, `FollowUpConversationTests.cs`, `FollowUpSettingsBindingTests.cs`.

**Modified files:**
- `McpChannelVoice/Settings/VoiceSettings.cs` — add `FollowUp`.
- `McpChannelVoice/Settings/SatelliteConfig.cs` — add `FollowUpEnabled` per-satellite override.
- `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs` — add no-speech-start timeout + `Decision.NoSpeech`.
- `McpChannelVoice/Services/SatelliteSession.cs` — mic routing, turn handshake, `PlaybackJob.OnDrained`.
- `McpChannelVoice/Services/WyomingSatelliteHost.cs` — read loop → demux + coordinator wiring + deferred transcript.
- `McpChannelVoice/McpTools/SendReplyTool.cs` — signal turn spoken/silent; reply job `OnDrained`.
- `McpChannelVoice/McpTools/RequestApprovalTool.cs` — capture sí/no via the session primitive (no re-wake).
- `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` — follow-up metrics.
- `McpChannelVoice/appsettings.json` — `FollowUp` defaults.
- Tests: `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`, `SatelliteSessionPlaybackTests.cs`, `RequestApprovalToolTests.cs`, `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`.

---

# Phase 1 — Foundation (pure units)

### Task 1: Follow-up settings, per-satellite override, metrics enum, appsettings

**Files:**
- Create: `McpChannelVoice/Settings/FollowUpSettings.cs`
- Modify: `McpChannelVoice/Settings/VoiceSettings.cs`
- Modify: `McpChannelVoice/Settings/SatelliteConfig.cs`
- Modify: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`
- Modify: `McpChannelVoice/appsettings.json`
- Test: `Tests/Unit/McpChannelVoice/FollowUpSettingsBindingTests.cs`

- [ ] **Step 1: Write the failing binding test**

Create `Tests/Unit/McpChannelVoice/FollowUpSettingsBindingTests.cs`:

```csharp
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class FollowUpSettingsBindingTests
{
    [Fact]
    public void Bind_Defaults_WhenSectionMissing()
    {
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentId"] = "mycroft" })
            .Build()
            .Get<VoiceSettings>()!;

        settings.FollowUp.Enabled.ShouldBeTrue();
        settings.FollowUp.WindowMs.ShouldBe(7000);
        settings.FollowUp.PlaybackTailMs.ShouldBe(400);
        settings.FollowUp.Chime.ShouldBeTrue();
        settings.FollowUp.MaxTurns.ShouldBe(8);
    }

    [Fact]
    public void Bind_OverridesAndPerSatelliteFlag()
    {
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FollowUp:Enabled"] = "false",
                ["FollowUp:WindowMs"] = "5000",
                ["Satellites:kitchen-01:Identity"] = "household",
                ["Satellites:kitchen-01:Room"] = "Kitchen",
                ["Satellites:kitchen-01:FollowUpEnabled"] = "true"
            })
            .Build()
            .Get<VoiceSettings>()!;

        settings.FollowUp.Enabled.ShouldBeFalse();
        settings.FollowUp.WindowMs.ShouldBe(5000);
        settings.Satellites["kitchen-01"].FollowUpEnabled.ShouldBe(true);
    }
}
```

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpSettingsBindingTests"`
Expected: FAIL — `VoiceSettings` has no `FollowUp`, `SatelliteConfig` has no `FollowUpEnabled`.

- [ ] **Step 3: Create `FollowUpSettings`**

Create `McpChannelVoice/Settings/FollowUpSettings.cs` (no trailing newline):

```csharp
namespace McpChannelVoice.Settings;

public record FollowUpSettings
{
    // Master switch. When false the channel behaves as before: the transcript is sent
    // immediately after each utterance and the satellite re-arms wake every turn.
    public bool Enabled { get; init; } = true;

    // How long the re-opened mic waits for the user to START speaking before the
    // conversation falls back to wake-required mode.
    public int WindowMs { get; init; } = 7000;

    // Echo guard: discard mic after playback for this long before opening the window,
    // letting speaker decay / room reverb settle.
    public int PlaybackTailMs { get; init; } = 400;

    // Play the listening earcon before each follow-up window.
    public bool Chime { get; init; } = true;

    // Runaway cap: fall back to wake after this many consecutive follow-up turns.
    public int MaxTurns { get; init; } = 8;
}
```

- [ ] **Step 4: Add `FollowUp` to `VoiceSettings`**

In `McpChannelVoice/Settings/VoiceSettings.cs`, add inside the record body (after `Satellites`):

```csharp
    public FollowUpSettings FollowUp { get; init; } = new();
```

- [ ] **Step 5: Add per-satellite override to `SatelliteConfig`**

In `McpChannelVoice/Settings/SatelliteConfig.cs`, add after `WakeWord`:

```csharp
    // Per-satellite override of FollowUpSettings.Enabled. Null inherits the global value.
    public bool? FollowUpEnabled { get; init; }
```

- [ ] **Step 6: Add metric enum values**

In `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`, add three values before the closing `}` (keep the existing trailing entry comma-correct — append after `AnnouncePreemptedReply`):

```csharp
    AnnouncePreemptedReply,
    FollowUpWindowOpened,
    FollowUpEngaged,
    FollowUpTimedOut
```

- [ ] **Step 7: Add appsettings defaults**

In `McpChannelVoice/appsettings.json`, add a `"FollowUp"` block after the `"WyomingClient"` block:

```json
    "FollowUp": {
        "Enabled": true,
        "WindowMs": 7000,
        "PlaybackTailMs": 400,
        "Chime": true,
        "MaxTurns": 8
    },
```

(No `.env` / docker-compose change: these are non-secret config, not env vars.)

- [ ] **Step 8: Run the test and verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpSettingsBindingTests"`
Expected: PASS (2 tests).

- [ ] **Step 9: Commit**

```bash
git add McpChannelVoice/Settings/FollowUpSettings.cs McpChannelVoice/Settings/VoiceSettings.cs McpChannelVoice/Settings/SatelliteConfig.cs Domain/DTOs/Metrics/Enums/VoiceMetric.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/FollowUpSettingsBindingTests.cs
git commit -m "feat(voice): add follow-up conversation settings and metrics"
```

---

### Task 2: `SilenceGate` no-speech-start timeout

**Files:**
- Modify: `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs` (inside the class, before the closing brace):

```csharp
    private static SilenceGate FollowUpGate() => new(
        rmsThreshold: 500,
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(10_000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(500));

    [Fact]
    public void Process_NoSpeechWithinWindow_ReturnsNoSpeech()
    {
        var gate = FollowUpGate();

        // 500 ms window / 100 ms per chunk => the 5th silent chunk crosses it.
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.NoSpeech);
    }

    [Fact]
    public void Process_SpeechBeforeWindowExpires_DoesNotReturnNoSpeech()
    {
        var gate = FollowUpGate();

        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.Continue);
        Feed(gate, Loud()).ShouldBe(SilenceGate.Decision.Continue);   // speech starts
        // Keep feeding past the no-speech window: speech started, so NoSpeech must never fire.
        foreach (var _ in Enumerable.Range(0, 8))
        {
            Feed(gate, Loud()).ShouldNotBe(SilenceGate.Decision.NoSpeech);
        }
    }

    [Fact]
    public void Process_NoSpeechTimeoutDisabledByDefault_NeverReturnsNoSpeech()
    {
        var gate = NewGate(); // default gate has noSpeechTimeout = default (disabled)

        foreach (var _ in Enumerable.Range(0, 30))
        {
            Feed(gate, Silent()).ShouldNotBe(SilenceGate.Decision.NoSpeech);
        }
    }
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests"`
Expected: FAIL — `Decision.NoSpeech` does not exist; the 5-arg constructor does not exist.

- [ ] **Step 3: Implement the no-speech timeout**

In `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`:

Change the primary constructor signature (add the 5th param with a disabled default):

```csharp
public sealed class SilenceGate(
    double rmsThreshold,
    TimeSpan trailingSilence,
    TimeSpan maxUtterance,
    TimeSpan minSpeech,
    TimeSpan noSpeechTimeout = default)
```

Add `NoSpeech` to the enum:

```csharp
    public enum Decision
    {
        Continue,
        EndUtterance,
        NoSpeech
    }
```

Replace the body of `Process` (the RMS branch onward) so the `else if (_speechStarted)` is followed by a no-speech branch:

```csharp
        if (Rms(pcm, sampleWidthBytes) >= rmsThreshold)
        {
            _speechStarted = true;
            _speechElapsed += duration;
            _trailingSilence = TimeSpan.Zero;
        }
        else if (_speechStarted)
        {
            _trailingSilence += duration;
            if (_speechElapsed > minSpeech && _trailingSilence >= trailingSilence)
            {
                return Decision.EndUtterance;
            }
        }
        else if (noSpeechTimeout > TimeSpan.Zero && _elapsed >= noSpeechTimeout)
        {
            return Decision.NoSpeech;
        }

        return _elapsed >= maxUtterance ? Decision.EndUtterance : Decision.Continue;
```

- [ ] **Step 4: Run and verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests"`
Expected: PASS (all existing + 3 new tests). The default-gate tests are unaffected because `noSpeechTimeout` defaults to `TimeSpan.Zero` (disabled).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs
git commit -m "feat(voice): add no-speech-start timeout to SilenceGate"
```

---

### Task 3: `ListeningChime` earcon generator

**Files:**
- Create: `McpChannelVoice/Services/ListeningChime.cs`
- Test: `Tests/Unit/McpChannelVoice/ListeningChimeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/ListeningChimeTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ListeningChimeTests
{
    [Fact]
    public void Pcm_Is16kMono180ms_AndNotSilent()
    {
        var pcm = ListeningChime.Pcm();

        // 0.18 s * 16000 Hz * 2 bytes = 5760 bytes.
        pcm.Length.ShouldBe(5760);
        pcm.Any(b => b != 0).ShouldBeTrue();
    }

    [Fact]
    public async Task Stream_YieldsOneWyomingStandardChunk()
    {
        var chunks = new List<AudioChunk>();
        await foreach (var c in ListeningChime.Stream())
        {
            chunks.Add(c);
        }

        chunks.Count.ShouldBe(1);
        chunks[0].Format.ShouldBe(AudioFormat.WyomingStandard);
        chunks[0].Data.Length.ShouldBe(5760);
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ListeningChimeTests"`
Expected: FAIL — `ListeningChime` does not exist.

- [ ] **Step 3: Implement `ListeningChime`**

Create `McpChannelVoice/Services/ListeningChime.cs`:

```csharp
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// A short rising two-tone earcon played before a wake-free follow-up window so the
// user knows the mic is open. Generated PCM (16 kHz/16-bit mono) — no asset file.
public static class ListeningChime
{
    private const double DurationSeconds = 0.18;

    public static byte[] Pcm(int sampleRateHz = 16_000)
    {
        var samples = (int)(sampleRateHz * DurationSeconds);
        var pcm = new byte[samples * 2];
        var fadeSamples = sampleRateHz * 0.01; // 10 ms in/out fade

        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / sampleRateHz;
            var freq = t < DurationSeconds / 2 ? 660.0 : 990.0;
            var fade = Math.Min(1.0, Math.Min(i, samples - i) / fadeSamples);
            var value = Math.Sin(2 * Math.PI * freq * t) * fade * 0.35;
            var s16 = (short)(value * short.MaxValue);
            pcm[i * 2] = (byte)(s16 & 0xFF);
            pcm[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }

        return pcm;
    }

    public static async IAsyncEnumerable<AudioChunk> Stream()
    {
        yield return new AudioChunk { Data = Pcm(), Format = AudioFormat.WyomingStandard };
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run and verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ListeningChimeTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/ListeningChime.cs Tests/Unit/McpChannelVoice/ListeningChimeTests.cs
git commit -m "feat(voice): add ListeningChime earcon generator"
```

---

# Phase 2 — Capture sink, session plumbing, coordinator

### Task 4: `UtteranceCapture` mic-capture sink

**Files:**
- Create: `McpChannelVoice/Services/UtteranceCapture.cs`
- Test: `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class UtteranceCaptureTests
{
    private const int Bytes = 3200; // 100 ms at 16 kHz/16-bit mono

    private static AudioChunk Loud()
    {
        var pcm = new byte[Bytes];
        for (var i = 0; i < pcm.Length; i += 2) { pcm[i] = 0x40; pcm[i + 1] = 0x1F; }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static AudioChunk Silent() =>
        new() { Data = new byte[Bytes], Format = AudioFormat.WyomingStandard };

    private static SilenceGate Gate(int noSpeechMs = 0) => new(
        rmsThreshold: 500,
        trailingSilence: TimeSpan.FromMilliseconds(200),
        maxUtterance: TimeSpan.FromMilliseconds(5000),
        minSpeech: TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: TimeSpan.FromMilliseconds(noSpeechMs));

    [Fact]
    public async Task Feed_SpeechThenSilence_CompletesEndedAndExposesAudio()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        var count = 0;
        await foreach (var _ in capture.Audio) { count++; }
        count.ShouldBe(4);
    }

    [Fact]
    public async Task Feed_OnlySilenceWithinWindow_CompletesNoSpeech()
    {
        var capture = new UtteranceCapture(Gate(noSpeechMs: 300));

        capture.Feed(Silent());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.NoSpeech);
    }

    [Fact]
    public async Task ForceEnd_CompletesEnded()
    {
        var capture = new UtteranceCapture(Gate());
        capture.Feed(Loud());
        capture.ForceEnd();
        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UtteranceCaptureTests"`
Expected: FAIL — `UtteranceCapture` and `CaptureOutcome` do not exist.

- [ ] **Step 3: Implement `UtteranceCapture`**

Create `McpChannelVoice/Services/UtteranceCapture.cs`:

```csharp
using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

public enum CaptureOutcome
{
    Ended,
    NoSpeech
}

// One bounded mic capture over the held-open Wyoming stream. The read loop pushes audio
// via Feed (single-threaded); the gate decides when speech ends (Ended) or the no-speech
// window expires (NoSpeech). Completed settles exactly once; Audio replays the buffered chunks.
public sealed class UtteranceCapture(SilenceGate gate)
{
    private readonly Channel<AudioChunk> _chunks = Channel.CreateUnbounded<AudioChunk>();
    private readonly TaskCompletionSource<CaptureOutcome> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CaptureOutcome> Completed => _done.Task;

    public IAsyncEnumerable<AudioChunk> Audio => _chunks.Reader.ReadAllAsync();

    public void Feed(AudioChunk chunk)
    {
        var decision = gate.Process(
            chunk.Data.Span, chunk.Format.SampleRateHz, chunk.Format.SampleWidthBytes, chunk.Format.Channels);
        _chunks.Writer.TryWrite(chunk);

        switch (decision)
        {
            case SilenceGate.Decision.EndUtterance:
                _chunks.Writer.TryComplete();
                _done.TrySetResult(CaptureOutcome.Ended);
                break;
            case SilenceGate.Decision.NoSpeech:
                _chunks.Writer.TryComplete();
                _done.TrySetResult(CaptureOutcome.NoSpeech);
                break;
        }
    }

    public void ForceEnd()
    {
        _chunks.Writer.TryComplete();
        _done.TrySetResult(CaptureOutcome.Ended);
    }
}
```

- [ ] **Step 4: Run and verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UtteranceCaptureTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/UtteranceCapture.cs Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs
git commit -m "feat(voice): add UtteranceCapture mic-capture sink"
```

---

### Task 5: `SatelliteSession` — mic routing, turn handshake, `PlaybackJob.OnDrained`

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Test: `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task RunPlaybackLoop_JobDrains_InvokesOnDrained()
    {
        var session = MakeSession();
        var drained = new List<string>();

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("hi", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnDrained: () => { drained.Add("reply:kitchen-01"); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(
            async (_, _) => await Task.Yield(), CancellationToken.None);

        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);
        session.CompletePlayback();
        await pump;

        drained.ShouldBe(["reply:kitchen-01"]);
    }

    [Fact]
    public async Task TurnHandshake_SignalSpoken_ResolvesTrue()
    {
        var session = MakeSession();
        session.ResetTurn();
        var wait = session.WaitForTurnSpokenAsync();
        session.SignalTurnSpoken();
        (await wait).ShouldBeTrue();
    }

    [Fact]
    public async Task TurnHandshake_SignalSilent_ResolvesFalse()
    {
        var session = MakeSession();
        session.ResetTurn();
        var wait = session.WaitForTurnSpokenAsync();
        session.SignalTurnSilent();
        (await wait).ShouldBeFalse();
    }

    [Fact]
    public void MicRouting_RouteAudio_FeedsActiveCaptureOnly()
    {
        var session = MakeSession();
        var chunk = new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard };

        // No active capture: routing is a no-op (must not throw).
        Should.NotThrow(() => session.RouteAudio(chunk));

        var capture = session.OpenCapture(new McpChannelVoice.Services.WyomingProtocol.SilenceGate(
            500, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)));

        session.RouteAudio(chunk); // silent -> still Continue
        session.CloseCapture();
        session.RouteAudio(chunk); // after close: no-op

        capture.ShouldNotBeNull();
    }
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionPlaybackTests"`
Expected: FAIL — `PlaybackJob` has no `OnDrained`; session has no `ResetTurn`/`WaitForTurnSpokenAsync`/`SignalTurnSpoken`/`SignalTurnSilent`/`OpenCapture`/`CloseCapture`/`RouteAudio`.

- [ ] **Step 3: Add `OnDrained` to `PlaybackJob`**

In `McpChannelVoice/Services/SatelliteSession.cs`, extend the record (append the optional param):

```csharp
public sealed record PlaybackJob(
    string Label,
    AnnouncePriority Priority,
    IAsyncEnumerable<AudioChunk> Audio,
    Func<string, Task> OnStarted,
    Func<string, Task> OnPreempted,
    Func<Task>? OnDrained = null);
```

- [ ] **Step 4: Fire `OnDrained` on successful drain**

In `RunPlaybackLoopAsync`, set a `drained` flag after the audio loop and invoke `OnDrained` in `finally`. Change the `try` body's tail (right after the `logger?.LogInformation("Playback job {Label} drained ...")` line) to set the flag, and add the invocation in `finally`. Concretely, declare `var drained = false;` next to `var chunks = 0;`, set `drained = true;` immediately after the `await foreach` over `job.Audio` completes, and in the `finally` block — after the existing `onAudioStop` section, before the `lock (_gate) { _currentPlaybackCts = null; }` — add:

```csharp
                if (drained && job.OnDrained is not null)
                {
                    try
                    {
                        await job.OnDrained();
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Playback OnDrained callback failed for {Label}", job.Label);
                    }
                }
```

(`drained` stays false on preemption and on audio faults, so `OnDrained` fires only after a full, successful drain.)

- [ ] **Step 5: Add mic routing + turn handshake to `SatelliteSession`**

Add these fields to the `SatelliteSession` class (next to `_playback`):

```csharp
    private volatile UtteranceCapture? _capture;
    private readonly Lock _turnGate = new();
    private TaskCompletionSource<bool> _turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
```

Add these methods to the class:

```csharp
    public UtteranceCapture OpenCapture(McpChannelVoice.Services.WyomingProtocol.SilenceGate gate)
    {
        var capture = new UtteranceCapture(gate);
        _capture = capture;
        return capture;
    }

    public void CloseCapture() => _capture = null;

    public bool HasActiveCapture => _capture is not null;

    public void RouteAudio(AudioChunk chunk) => _capture?.Feed(chunk);

    public void EndCapture() => _capture?.ForceEnd();

    public void ResetTurn()
    {
        lock (_turnGate)
        {
            _turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task<bool> WaitForTurnSpokenAsync()
    {
        lock (_turnGate)
        {
            return _turn.Task;
        }
    }

    public void SignalTurnSpoken()
    {
        lock (_turnGate)
        {
            _turn.TrySetResult(true);
        }
    }

    public void SignalTurnSilent()
    {
        lock (_turnGate)
        {
            _turn.TrySetResult(false);
        }
    }
```

Add the `using` for the SilenceGate namespace at the top of the file if not present:

```csharp
using McpChannelVoice.Services.WyomingProtocol;
```

…and simplify the `OpenCapture`/method signatures to use `SilenceGate` directly (drop the fully-qualified name) once the using is added.

- [ ] **Step 6: Run and verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionPlaybackTests"`
Expected: PASS (existing 3 + 4 new).

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs
git commit -m "feat(voice): add mic routing, turn handshake, and playback OnDrained to SatelliteSession"
```

---

### Task 6: `FollowUpConversation` coordinator

**Files:**
- Create: `McpChannelVoice/Services/FollowUpConversation.cs`
- Test: `Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs`

The coordinator owns one connection's turn loop. It opens/closes captures and orchestrates dispatch → await reply → chime → tail → re-open, ending the conversation on no-speech, silent turn, cap, or when disabled. All I/O is injected as delegates for unit testing; it never touches audio bytes (the session routes those into the capture it opened).

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class FollowUpConversationTests
{
    private static SilenceGate AnyGate(bool followUp) => new(
        500, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5000),
        TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: followUp ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero);

    private sealed class Harness
    {
        public readonly List<string> Events = [];
        public readonly FakeTimeProvider Time = new(DateTimeOffset.UtcNow);
        public readonly List<UtteranceCapture> Opened = [];
        private TaskCompletionSource<bool> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FollowUpConversation Build(FollowUpSettings followUp) => new(
            followUp,
            new WyomingClientSettings(),
            Time,
            NullLogger.Instance)
        {
            OpenCapture = isFollowUp =>
            {
                var c = new UtteranceCapture(AnyGate(isFollowUp));
                Opened.Add(c);
                Events.Add(isFollowUp ? "open-followup" : "open-first");
                return c;
            },
            CloseCapture = () => { },
            TranscribeAndDispatch = (_, isFollowUp, _) =>
            {
                Events.Add(isFollowUp ? "dispatch-followup" : "dispatch-first");
                return Task.CompletedTask;
            },
            EnqueueChime = _ => { Events.Add("chime"); return Task.CompletedTask; },
            EndConversation = _ => { Events.Add("end"); return Task.CompletedTask; },
            ResetTurn = () => _reply = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AwaitReply = () => _reply.Task,
            OnFollowUpWindow = _ => Task.CompletedTask
        };

        public void Reply(bool spoke) => _reply.TrySetResult(spoke);
    }

    [Fact]
    public async Task Disabled_DispatchesThenEndsImmediately_NoReplyWait()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd(); // utterance ended (speech)

        await Task.Delay(50);
        h.Events.ShouldBe(["open-first", "dispatch-first", "end"]);

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SpeechReplyThenFollowUp_OpensSecondWindowWithoutWake()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = true, PlaybackTailMs = 400, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();          // first utterance ends
        await Task.Delay(50);
        h.Reply(spoke: true);            // agent reply spoken
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMilliseconds(400)); // tail
        await Task.Delay(50);

        h.Events.ShouldContain("chime");
        h.Events.ShouldContain("open-followup");

        // The follow-up window opened a second capture without a new wake.
        h.Opened.Count.ShouldBe(2);

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_FollowUpSilence_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();
        await Task.Delay(50);
        h.Reply(spoke: true);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        // Second capture is the follow-up window; feeding only silence => NoSpeech => end.
        var followUp = h.Opened[1];
        var silent = new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard };
        for (var i = 0; i < 6; i++) { followUp.Feed(silent); }

        await Task.Delay(50);
        h.Events.ShouldContain("end");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SilentTurn_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();
        await Task.Delay(50);
        h.Reply(spoke: false); // agent produced no audio

        await Task.Delay(50);
        h.Events.ShouldContain("end");
        h.Events.ShouldNotContain("chime");

        await StopAsync(sut, run);
    }

    private static async Task StopAsync(FollowUpConversation sut, Task run)
    {
        sut.Dispose();
        try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* unwinds on dispose/cancel */ }
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpConversationTests"`
Expected: FAIL — `FollowUpConversation` does not exist.

- [ ] **Step 3: Implement `FollowUpConversation`**

Create `McpChannelVoice/Services/FollowUpConversation.cs`:

```csharp
using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging;

namespace McpChannelVoice.Services;

// Per-connection turn-taking over one held-open Wyoming wake stream. Runs on its own task;
// the read loop calls OnWake/OnAudioStop and routes audio into the session capture this opens.
// I/O is injected as delegates so the loop is unit-testable without TCP, STT, or playback.
public sealed class FollowUpConversation(
    FollowUpSettings followUp,
    WyomingClientSettings wyoming,
    TimeProvider time,
    ILogger logger) : IDisposable
{
    private readonly Channel<bool> _wakes = Channel.CreateUnbounded<bool>();
    private readonly CancellationTokenSource _disposed = new();
    private volatile UtteranceCapture? _first;
    private volatile bool _active;

    // Opens a capture on the session (returns it) — isFollowUp selects the no-speech window.
    public required Func<bool, UtteranceCapture> OpenCapture { get; init; }
    public required Action CloseCapture { get; init; }

    // Transcribe the captured audio and dispatch it to the agent.
    public required Func<IAsyncEnumerable<AudioChunk>, bool, CancellationToken, Task> TranscribeAndDispatch { get; init; }

    // Enqueue the chime and return once it has drained.
    public required Func<CancellationToken, Task> EnqueueChime { get; init; }

    // Write the closing transcript to the satellite (stops streaming, re-arms wake).
    public required Func<CancellationToken, Task> EndConversation { get; init; }

    // Reset / await the per-turn "did the agent speak?" handshake.
    public required Action ResetTurn { get; init; }
    public required Func<Task<bool>> AwaitReply { get; init; }

    // Side effect (metric) just before a follow-up window opens.
    public required Func<CancellationToken, Task> OnFollowUpWindow { get; init; }

    public void OnWake()
    {
        if (_active)
        {
            return;
        }
        _active = true;
        _first = OpenCapture(false);
        _wakes.Writer.TryWrite(true);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _wakes.Reader.ReadAsync(token);
                await RunConversationAsync(token);
                _active = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Connection tearing down.
        }
    }

    private async Task RunConversationAsync(CancellationToken ct)
    {
        var capture = _first!;
        var turns = 0;

        while (!ct.IsCancellationRequested)
        {
            var outcome = await capture.Completed.WaitAsync(ct);
            CloseCapture();

            if (outcome == CaptureOutcome.NoSpeech)
            {
                await EndConversation(ct);
                return;
            }

            var isFollowUp = turns > 0;
            ResetTurn();
            await TranscribeAndDispatch(capture.Audio, isFollowUp, ct);

            if (!followUp.Enabled)
            {
                await EndConversation(ct);
                return;
            }

            var spoke = await AwaitReply().WaitAsync(ct);
            if (!spoke || turns >= followUp.MaxTurns)
            {
                await EndConversation(ct);
                return;
            }

            if (followUp.Chime)
            {
                await EnqueueChime(ct);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(followUp.PlaybackTailMs), time, ct);

            turns++;
            await OnFollowUpWindow(ct);
            capture = OpenCapture(true);
        }
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }
}
```

- [ ] **Step 4: Run and verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpConversationTests"`
Expected: PASS (4 tests). If timing is flaky on a slow machine, the `await Task.Delay(50)` waits can be increased — they only sequence the cooperating task, not assert timing.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/FollowUpConversation.cs Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs
git commit -m "feat(voice): add FollowUpConversation turn coordinator"
```

---

# Phase 3 — Host integration

### Task 7: Wire the coordinator into `WyomingSatelliteHost`

**Files:**
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs`
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Modify: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`

This task replaces the read loop's inline utterance logic with: (a) frame demux → `session.RouteAudio`/`EndCapture` + `coordinator.OnWake`; (b) a coordinator whose delegates do the STT/dispatch/chime/transcript I/O; (c) deferred transcript. `SendReplyTool` signals the turn handshake.

- [ ] **Step 1: Add `TimeProvider` to the host constructor**

In `WyomingSatelliteHost`, add `TimeProvider time` to the primary constructor parameter list (e.g. after `IMetricsPublisher metrics`). It is already DI-registered (`ConfigModule.cs:45`), so `AddHostedService<WyomingSatelliteHost>()` resolves it.

- [ ] **Step 2: Rewrite `RunConnectionAsync` and replace `BeginUtterance`/`TranscribeAndReplyAsync`**

Replace the body from the `playbackTask` wiring through the end of `TranscribeAndReplyAsync` with the version below. Keep `StartAsync`/`StopAsync`/`ConnectionLoopAsync`/`WritePlaybackFrameAsync`/`ToChunk`/`FormatOf`/`TryParseAddress` unchanged.

Replace `RunConnectionAsync` with:

```csharp
    private async Task RunConnectionAsync(string id, SatelliteConfig config, string host, int port, CancellationToken ct)
    {
        await using var client = new WyomingClient();
        await client.ConnectAsync(host, port, ct);

        var session = new SatelliteSession(id, config);
        sessionRegistry.Register(session);
        logger.LogInformation("Connected to satellite {Id} at {Host}:{Port}", id, host, port);

        var playbackTask = Task.Run(() => session.RunPlaybackLoopAsync(
            (chunk, jct) => WritePlaybackFrameAsync(client, chunk, jct),
            ct, logger,
            onAudioStart: (format, sct) => client.WriteAsync(WyomingEvent.Header("audio-start", new JsonObject
            {
                ["rate"] = format.SampleRateHz,
                ["width"] = format.SampleWidthBytes,
                ["channels"] = format.Channels,
                ["timestamp"] = 0
            }), sct),
            onAudioStop: sct => client.WriteAsync(
                WyomingEvent.Header("audio-stop", new JsonObject { ["timestamp"] = 0 }), sct),
            onError: async (job, ex) =>
            {
                try
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.TtsError,
                        SatelliteId = id,
                        Room = config.Room,
                        Identity = config.Identity,
                        Error = ex.Message,
                        ConversationId = conversationManager.GetActiveConversationId(id)
                    }, ct);
                }
                catch (Exception mex)
                {
                    logger.LogWarning(mex, "Failed to publish TtsError metric for {Id} ({Label})", id, job.Label);
                }
            }), ct);

        var followUp = voiceSettings.FollowUp with
        {
            Enabled = config.FollowUpEnabled ?? voiceSettings.FollowUp.Enabled
        };

        var coordinator = BuildCoordinator(id, config, client, session, followUp);
        var conversationTask = Task.Run(() => coordinator.RunAsync(ct), ct);

        try
        {
            await client.WriteAsync(WyomingEvent.Header("run-satellite", new JsonObject()), ct);

            await foreach (var evt in client.ReadAllAsync(ct))
            {
                switch (evt.Type)
                {
                    case "run-pipeline":
                    case "audio-start":
                        coordinator.OnWake();
                        break;

                    case "audio-chunk":
                        var (rate, width, channels) = FormatOf(evt.Data);
                        session.RouteAudio(ToChunk(evt.Payload, rate, width, channels));
                        break;

                    case "audio-stop":
                        session.EndCapture();
                        break;

                    case "error":
                        logger.LogWarning("Satellite {Id} reported error: {Message}",
                            id, evt.Data["text"]?.GetValue<string>());
                        break;
                }
            }
        }
        finally
        {
            coordinator.Dispose();
            session.CompletePlayback();
            try { await playbackTask; } catch { /* unwinds on cancellation / disconnect */ }
            try { await conversationTask; } catch { /* unwinds on cancellation / disconnect */ }
            sessionRegistry.Unregister(id);
        }
    }
```

Add the coordinator factory (delegates carry all the per-connection I/O). Replace the old `BeginUtterance` and `TranscribeAndReplyAsync` methods with these two:

```csharp
    private FollowUpConversation BuildCoordinator(
        string id, SatelliteConfig config, WyomingClient client, SatelliteSession session, FollowUpSettings followUp)
    {
        return new FollowUpConversation(followUp, time)
        {
            OpenCapture = isFollowUp =>
            {
                if (!isFollowUp)
                {
                    PublishVoiceMetric(VoiceMetric.WakeTriggered, session); // on-device wake started this conversation
                }
                return session.OpenCapture(new SilenceGate(
                    settings.SilenceRmsThreshold,
                    TimeSpan.FromMilliseconds(settings.TrailingSilenceMs),
                    TimeSpan.FromMilliseconds(settings.MaxUtteranceMs),
                    TimeSpan.FromMilliseconds(settings.MinSpeechMs),
                    noSpeechTimeout: isFollowUp ? TimeSpan.FromMilliseconds(followUp.WindowMs) : TimeSpan.Zero));
            },
            CloseCapture = session.CloseCapture,
            TranscribeAndDispatch = (audio, isFollowUp, token) =>
                TranscribeAndDispatchAsync(session, audio, isFollowUp, token),
            EnqueueChime = token => EnqueueChimeAsync(session, token),
            EndConversation = token => client.WriteAsync(
                WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), token),
            ResetTurn = session.ResetTurn,
            AwaitReply = session.WaitForTurnSpokenAsync,
            OnFollowUpWindow = token =>
            {
                PublishVoiceMetric(VoiceMetric.FollowUpWindowOpened, session);
                return Task.CompletedTask;
            }
        };
    }

    private async Task TranscribeAndDispatchAsync(
        SatelliteSession session, IAsyncEnumerable<AudioChunk> audio, bool isFollowUp, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await speechToText.TranscribeAsync(audio, new TranscriptionOptions(), ct);
            sw.Stop();

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttLatencyMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = sw.ElapsedMilliseconds,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            }, ct);

            if (isFollowUp)
            {
                PublishVoiceMetric(VoiceMetric.FollowUpEngaged, session);
            }

            await dispatcher.DispatchAsync(session, result, voiceSettings.AgentId, ct);
        }
        catch (OperationCanceledException)
        {
            // Connection tearing down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription failed for {Id}", session.SatelliteId);
            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttError,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Error = ex.Message,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            }, ct);
        }
    }

    private async Task EnqueueChimeAsync(SatelliteSession session, CancellationToken ct)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new PlaybackJob(
            Label: $"chime:{session.SatelliteId}",
            Priority: AnnouncePriority.High,
            Audio: ListeningChime.Stream(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnDrained: () => { drained.TrySetResult(); return Task.CompletedTask; });

        await session.EnqueuePlaybackAsync(job, voiceSettings.Announce.QueueMaxDepth);
        await drained.Task.WaitAsync(ct);
    }

    private void PublishVoiceMetric(VoiceMetric metric, SatelliteSession session) =>
        _ = metrics.PublishAsync(new VoiceEvent
        {
            Metric = metric,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        }, CancellationToken.None);
```

> Note: `EndConversation` writes an empty `transcript` — the satellite ignores the text and uses it only to stop streaming and re-arm wake. `FollowUpTimedOut` is published from the coordinator's no-speech path; publish it inside `EndConversation` only when reached via no-speech. Keep it simple: publish `FollowUpTimedOut` in `EndConversation` unconditionally is acceptable for v1 (it marks "conversation ended → back to wake"); if finer granularity is wanted later, thread the outcome through. For this task, add the publish to `EndConversation`:

```csharp
            EndConversation = token =>
            {
                PublishVoiceMetric(VoiceMetric.FollowUpTimedOut, session);
                return client.WriteAsync(
                    WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), token);
            },
```

Remove the now-unused `Channel<AudioChunk>`/`SilenceGate` locals and the `using System.Threading.Channels;` import only if no longer referenced (the `WritePlaybackFrameAsync`/`ToChunk` helpers do not need it). Keep `using System.Diagnostics;` (Stopwatch) and the metrics/voice usings.

- [ ] **Step 3: Signal the turn handshake from `SendReplyTool`**

In `McpChannelVoice/McpTools/SendReplyTool.cs`:

(a) In `HandleUtteranceReplyAsync`, the `StreamComplete` case must signal silent when nothing was spoken. Change `FlushAndSpeakAsync` to return whether it spoke and signal accordingly:

Replace the `StreamComplete` case body:

```csharp
            case ReplyContentType.StreamComplete:
                var spoke = await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics);
                if (!spoke)
                {
                    session.SignalTurnSilent();
                }
                return "ok";
```

Change `FlushAndSpeakAsync` to return `bool`:

```csharp
    private static async Task<bool> FlushAndSpeakAsync(
        SatelliteSession session,
        ReplyTextAccumulator accumulator,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics)
    {
        var text = accumulator.Flush(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        await SpeakAsync(session, text, conversationId, tts, settings, metrics, default);
        return true;
    }
```

Update the other caller (the `default` case's `if (p.IsComplete)` branch) to ignore the return value (`_ = await FlushAndSpeakAsync(...)`) or leave it (a discard is fine).

(b) In `SpeakAsync`, set the reply job's `OnDrained` to signal the turn spoken so the coordinator opens the follow-up window. Change the `PlaybackJob` construction to add:

```csharp
            OnPreempted: async _ =>
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnouncePreemptedReply,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    ConversationId = conversationId
                }, ct);
            },
            OnDrained: () => { session.SignalTurnSpoken(); return Task.CompletedTask; });
```

(Only the `reply:` jobs created by `SendReplyTool.SpeakAsync` get this `OnDrained`; approval and announcement jobs do not, so they never signal the conversation turn.)

- [ ] **Step 4: Update the existing integration test for the disabled (legacy) path**

In `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`:

(a) The host constructor now takes a `TimeProvider`. Add it to the `new WyomingSatelliteHost(...)` call — pass `TimeProvider.System` as the new last-but-one argument (matching the parameter position you chose in Step 1), e.g. after `publisher.Object`:

```csharp
            registry, sessions, manager, stt.Object, dispatcher, publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance);
```

(b) The existing test asserts an immediate transcript after one utterance. Pin it to the legacy path by disabling follow-up in its `VoiceSettings`:

```csharp
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = false } },
```

The disabled path dispatches then writes the closing transcript immediately. Because `EndConversation` now writes an **empty** transcript, change the final assertion from `transcriptText.ShouldBe("hola")` to:

```csharp
        var transcriptText = await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        transcriptText.ShouldBe(""); // legacy path re-arms with an (ignored) empty transcript
```

Add `using McpChannelVoice.Settings;` if not already imported (it is — `SatelliteConfig`/`WyomingClientSettings` come from there).

- [ ] **Step 5: Add an integration test for the enabled follow-up path**

Append to `WyomingSatelliteHostTests.cs` a second `[Fact]` that proves a wake-free follow-up. It reuses the TCP fake-satellite harness; it simulates the agent reply by signalling the session (the realistic `reply:` → `OnDrained` → `SignalTurnSpoken` path is covered by Task 5/SendReplyTool unit tests).

```csharp
    [Fact]
    public async Task Hub_FollowUpEnabled_DispatchesFollowUpWithoutSecondWake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawTranscript = new TaskCompletionSource<string>();
        var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };

        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);
            var sawRun = new TaskCompletionSource();

            var readLoop = Task.Run(async () =>
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    if (evt.Type == "run-satellite") { sawRun.TrySetResult(); }
                    else if (evt.Type == "transcript") { sawTranscript.TrySetResult(evt.Data["text"]?.GetValue<string>() ?? ""); }
                    // audio-start/audio-chunk/audio-stop (TTS + chime playback) are drained and ignored.
                }
            }, ct);

            await sawRun.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);

            // First utterance: speech then trailing silence.
            foreach (var _ in Enumerable.Range(0, 4))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(8000)), ct);
            }
            foreach (var _ in Enumerable.Range(0, 6))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }

            // The held-open stream never stops on its own; pause briefly while the host dispatches
            // the first utterance and the test simulates the agent's spoken reply, then continue.
            await Task.Delay(300, ct);

            // Follow-up utterance: more speech then silence — NO new run-pipeline (wake-free).
            foreach (var _ in Enumerable.Range(0, 4))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(8000)), ct);
            }
            foreach (var _ in Enumerable.Range(0, 6))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }

            await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }, ct);

        // Two dispatched utterances expected: capture both.
        var dispatched = new List<string>();
        var bothDispatched = new TaskCompletionSource();
        var emitter = new CollectingEmitter(dispatched, bothDispatched, expected: 2);

        var stt = new Mock<ISpeechToText>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(async (audio, _, token) =>
            {
                await foreach (var _ in audio.WithCancellation(token)) { }
                return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
            });

        var publisher = new Mock<IMetricsPublisher>();
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1", "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        var manager = new VoiceConversationManager(factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var dispatcher = new TranscriptDispatcher(emitter, publisher.Object, new ApprovalCaptureBroker(), manager, 0.4, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis", Address = $"tcp://127.0.0.1:{port}" }
        });

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings { ReconnectDelaySeconds = 1, SilenceRmsThreshold = 500, TrailingSilenceMs = 200, MaxUtteranceMs = 3000, MinSpeechMs = 100 },
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 } },
            registry, sessions, manager, stt.Object, dispatcher, publisher.Object, TimeProvider.System, NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        // First utterance dispatched -> simulate the agent's spoken reply so the follow-up window opens.
        await WaitForCountAsync(dispatched, 1, TimeSpan.FromSeconds(10));
        sawTranscript.Task.IsCompleted.ShouldBeFalse(); // transcript deferred (no re-arm yet)
        sessions.Get("kitchen-01").ShouldNotBeNull();
        sessions.Get("kitchen-01")!.SignalTurnSpoken();

        // Second utterance must be dispatched WITHOUT a second run-pipeline (wake-free follow-up).
        await bothDispatched.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        dispatched.Count.ShouldBe(2);

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try { await fakeSatellite; } catch { }
    }

    private static async Task WaitForCountAsync(List<string> list, int count, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (list.Count < count)
        {
            if (sw.Elapsed > timeout) { throw new TimeoutException($"only {list.Count}/{count}"); }
            await Task.Delay(20);
        }
    }

    private sealed class CollectingEmitter(List<string> sink, TaskCompletionSource done, int expected)
        : ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance)
    {
        public override Task EmitMessageNotificationAsync(
            string conversationId, string sender, string content, string? agentId, string? location, string? satelliteId, CancellationToken ct = default)
        {
            lock (sink)
            {
                sink.Add(content);
                if (sink.Count >= expected) { done.TrySetResult(); }
            }
            return Task.CompletedTask;
        }
    }
```

> The `await Task.Delay(300)` in the fake satellite is a simple sequencer: it streams the first utterance, pauses while the host dispatches + the test signals the reply, then streams the follow-up. If flaky, raise the delay or gate it on a shared `TaskCompletionSource` the test completes after `SignalTurnSpoken`. The assertion that matters is **two dispatched utterances from a single `run-pipeline`**.

- [ ] **Step 6: Build and run the voice tests**

Run: `dotnet build Agent.sln`
Then: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"`
Expected: PASS for all `McpChannelVoice` unit + integration tests (both host tests included). Investigate any failure before committing.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/McpTools/SendReplyTool.cs Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs
git commit -m "feat(voice): hold the wake stream open for wake-free follow-up turns"
```

---

# Phase 4 — Approval capture without re-wake

### Task 8: Capture sí/no via the session primitive in `RequestApprovalTool`

**Files:**
- Modify: `McpChannelVoice/McpTools/RequestApprovalTool.cs`
- Test: `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`

Today the `Request` path speaks a prompt then `broker.WaitForUtteranceAsync` — which only resolves if the user re-wakes and the dispatched transcript is routed to the broker. We replace that with a direct post-prompt capture: speak the prompt, await its playback drain, tail-guard, open a capture window on the session, transcribe it, parse — no re-wake. The mode enum is `ApprovalMode.Notify | ApprovalMode.Request` (there is no `Ask`); only the `Request` path changes.

The three existing `Request`-mode tests drive the answer via `_broker.SubmitUtterance(...)`. Since the broker is no longer used, they must be rewritten to drive the new session capture. The `Notify`-mode and unknown-conversation tests are unchanged.

- [ ] **Step 1: Extend the test fixture (STT, settings, playback pump, audio feeder)**

In `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`, make the class `IDisposable`, add an STT mock + a background playback pump (so the prompt job drains and `OnDrained` fires), and register the new services the tool resolves.

Add fields:

```csharp
    private readonly Mock<ISpeechToText> _stt = new();
    private readonly CancellationTokenSource _pump = new();
```

In the constructor, **after** `_sessions.Register(_session);`, start a playback pump that drains audio so enqueued jobs complete:

```csharp
        _ = _session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), _pump.Token);
```

Change the service registration block to add `ISpeechToText`, `WyomingClientSettings`, and a follow-up-tuned `VoiceSettings` (replace the existing `.AddSingleton(new VoiceSettings())` line):

```csharp
            .AddSingleton(new VoiceSettings
            {
                FollowUp = new FollowUpSettings { PlaybackTailMs = 0, WindowMs = 2000 }
            })
            .AddSingleton<ISpeechToText>(_stt.Object)
            .AddSingleton(new WyomingClientSettings
            {
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 3000,
                MinSpeechMs = 100
            })
```

Add `Dispose`, audio helpers, and a feeder that completes any open capture with speech-then-silence:

```csharp
    public void Dispose()
    {
        _pump.Cancel();
        _session.CompletePlayback();
        _pump.Dispose();
    }

    private static AudioChunk Loud()
    {
        var pcm = new byte[3200];
        for (var i = 0; i < pcm.Length; i += 2) { pcm[i] = 0x40; pcm[i + 1] = 0x1F; }
        return new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard };
    }

    private static AudioChunk Silent() =>
        new() { Data = new byte[3200], Format = AudioFormat.WyomingStandard };

    // Whenever the tool opens a capture, feed one speech-then-silence answer into it.
    private Task FeedAnswersAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            if (_session.HasActiveCapture)
            {
                _session.RouteAudio(Loud());
                _session.RouteAudio(Loud());
                _session.RouteAudio(Silent());
                _session.RouteAudio(Silent());
                _session.RouteAudio(Silent());
                await Task.Delay(60, ct);
            }
            else
            {
                await Task.Delay(10, ct);
            }
        }
    }, ct);
```

- [ ] **Step 2: Rewrite the three `Request`-mode tests to drive the capture**

Replace `RequestMode_PositiveAnswer_ReturnsApproved`, `RequestMode_AmbiguousThenNegative_ReturnsDeclined`, and `RequestMode_TwoAmbiguous_DeclinesByDefault` with capture-driven versions (the STT mock supplies the recognized answer per attempt):

```csharp
    [Fact]
    public async Task RequestMode_PositiveAnswer_ReturnsApproved()
    {
        _stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "sí, claro", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task RequestMode_AmbiguousThenNegative_ReturnsDeclined()
    {
        _stt.SetupSequence(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "maybe", Confidence = 0.9 })
            .ReturnsAsync(new TranscriptionResult { Text = "no thanks", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("declined");
    }

    [Fact]
    public async Task RequestMode_TwoAmbiguous_DeclinesByDefault()
    {
        _stt.SetupSequence(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptionResult { Text = "maybe", Confidence = 0.9 })
            .ReturnsAsync(new TranscriptionResult { Text = "hmm", Confidence = 0.9 });

        using var feed = new CancellationTokenSource();
        var feeder = FeedAnswersAsync(feed.Token);

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request, [MakeRequest()], _services);

        await feed.CancelAsync();
        result.ShouldBe("declined");
    }
```

- [ ] **Step 3: Run and verify failure**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: FAIL — the `Request` path still calls `broker.WaitForUtteranceAsync` (no capture is opened, so `HasActiveCapture` is never true and the call hangs/times out or the STT mock is never hit).

- [ ] **Step 4: Implement the direct capture**

In `RequestApprovalTool.McpRun` (the `Request` branch — i.e. the `else` after the `Notify` early-return), resolve the extra services and replace the `broker.WaitForUtteranceAsync` loop with a session capture. Add to the resolved services:

```csharp
        var stt = services.GetRequiredService<ISpeechToText>();
        var wyoming = services.GetRequiredService<WyomingClientSettings>();
        var followUp = settings.FollowUp;
```

Replace the `for (var attempt ...)` loop's prompt-and-wait step. `SpeakAndAwaitAsync` enqueues the prompt and returns once it has finished playing (so the capture opens against a quiet mic); `CaptureAnswerAsync` then opens the window and transcribes:

```csharp
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await SpeakAndAwaitAsync(session, prompt, tts, settings);

            var answer = await CaptureAnswerAsync(session, stt, wyoming, followUp, default);
            var parsed = ApprovalGrammarParser.Parse(answer);

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.ApprovalResolved,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Outcome = parsed.ToString(),
                ConversationId = p.ConversationId
            }, default);

            switch (parsed)
            {
                case ApprovalResponse.Approved:
                    return "approved";
                case ApprovalResponse.Declined:
                    return "declined";
            }

            prompt = $"No entendí. ¿Apruebas {toolList}? Di sí o no.";
        }

        return "declined";
```

Add the helpers to the class. `SpeakAndAwaitAsync` mirrors the existing private `SpeakAsync` but awaits the prompt's playback drain via `OnDrained` (the connection's playback loop is always running in production; the unit-test fixture starts a pump for the same reason):

```csharp
    private static async Task SpeakAndAwaitAsync(
        SatelliteSession session, string text, ITextToSpeech tts, VoiceSettings settings)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Priority: AnnouncePriority.High,
            Audio: tts.SynthesizeAsync(text, new SynthesisOptions { Voice = voice }, default),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => { drained.TrySetResult(); return Task.CompletedTask; },
            OnDrained: () => { drained.TrySetResult(); return Task.CompletedTask; });

        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
        await drained.Task;
    }

    private static async Task<string> CaptureAnswerAsync(
        SatelliteSession session, ISpeechToText stt, WyomingClientSettings wyoming,
        FollowUpSettings followUp, CancellationToken ct)
    {
        if (followUp.PlaybackTailMs > 0)
        {
            await Task.Delay(followUp.PlaybackTailMs, ct); // echo guard after the prompt finishes
        }

        var capture = session.OpenCapture(new SilenceGate(
            wyoming.SilenceRmsThreshold,
            TimeSpan.FromMilliseconds(wyoming.TrailingSilenceMs),
            TimeSpan.FromMilliseconds(wyoming.MaxUtteranceMs),
            TimeSpan.FromMilliseconds(wyoming.MinSpeechMs),
            noSpeechTimeout: TimeSpan.FromMilliseconds(followUp.WindowMs)));

        var outcome = await capture.Completed;
        session.CloseCapture();

        if (outcome == CaptureOutcome.NoSpeech)
        {
            return string.Empty;
        }

        var result = await stt.TranscribeAsync(capture.Audio, new TranscriptionOptions(), ct);
        return result.Text ?? string.Empty;
    }
```

Add the needed `using McpChannelVoice.Services.WyomingProtocol;` import. Leave `ApprovalCaptureBroker` registered (the dispatcher still references it); the Ask path no longer uses it.

> Composition note: an approval happens mid agent-turn while the conversation coordinator is parked in `AwaitReply` (capture closed). The tool opening/closing the session capture during that window does not collide with the coordinator, which only re-opens a capture after the turn's reply drains.

- [ ] **Step 5: Run and verify pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: PASS — the two `Notify` tests and the unknown-conversation test are unchanged; the three rewritten `Request` tests now pass via the session capture. No test references `_broker.SubmitUtterance` anymore.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/McpTools/RequestApprovalTool.cs Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs
git commit -m "feat(voice): capture voice approvals without re-waking"
```

---

# Phase 5 — Verification

### Task 9: Full build, suite run, and hardware validation checklist

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build Agent.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 2: Run the full non-E2E suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E"`
Expected: All **new** voice tests pass. Pre-existing `DockerUnavailableException` failures (~148 in this WSL env) are the baseline — confirm no *new* failures in `McpChannelVoice`/`Domain` beyond that baseline.

- [ ] **Step 3: Confirm dotnet format is clean for touched files**

Run: `dotnet format Agent.sln --verify-no-changes` (top-level `Program.cs` files are a known-dirty baseline — ignore those; ensure none of the files this plan touched are reported).

- [ ] **Step 4: Manual hardware validation (record results in the PR description)**

Bring up the stack (`docker compose ... up -d --build agent mcp-channel-voice wyoming-whisper wyoming-piper ...`) with at least one real satellite configured (`Voice:Satellites:<id>:Address`). Then verify on the device:

1. Wake → ask a question → agent answers → **chime plays** → speak a follow-up **without the wake word** → agent answers the follow-up. ✅ wake-free chaining.
2. After a reply + chime, **stay silent** for `WindowMs` → satellite plays its "done" sound and returns to wake-required (next utterance needs the wake word). ✅ silence fallback.
3. **Echo check:** the agent's own speech and the chime do **not** trigger a phantom follow-up (tune `PlaybackTailMs` up if the window self-triggers; tune `SilenceRmsThreshold` if needed). ✅ no self-trigger.
4. Run a conversation past `MaxTurns` → it falls back to wake. ✅ runaway cap.
5. Approval: trigger a tool that asks for approval → answer **sí** without re-waking → approved. ✅ approval capture.
6. Set `Voice:Satellites:<id>:FollowUpEnabled=false` → that satellite behaves as before (wake every turn). ✅ per-satellite override.

- [ ] **Step 5: Final commit (if any tuning constants changed during validation)**

```bash
git add -A
git commit -m "chore(voice): tune follow-up window/tail defaults after hardware validation"
```

---

## Self-Review notes (for the implementer)

- **Type consistency:** `CaptureOutcome { Ended, NoSpeech }`; `SilenceGate.Decision { Continue, EndUtterance, NoSpeech }`; session methods `OpenCapture/CloseCapture/RouteAudio/EndCapture/HasActiveCapture/ResetTurn/WaitForTurnSpokenAsync/SignalTurnSpoken/SignalTurnSilent`; `PlaybackJob.OnDrained`; coordinator delegates `OpenCapture/CloseCapture/TranscribeAndDispatch/EnqueueChime/EndConversation/ResetTurn/AwaitReply/OnFollowUpWindow`. These names are used identically across tasks — do not rename.
- **Spec coverage:** held-open stream (Task 7), echo suppression via `null` capture + tail (Tasks 5–7), no-speech timeout (Task 2), chime (Tasks 3, 7), turn-completion coordination (Tasks 5, 7), MaxTurns cap (Task 6), config + per-satellite override (Task 1), metrics (Tasks 1, 7), approval integration (Task 8), thread continuity (unchanged — `VoiceConversationManager`), failure handling (`EndConversation` on every exit; `TranscribeAndDispatchAsync` swallows + re-publishes errors).
- **Out of scope (do not build):** barge-in, on-device shim, VAD-only mode.
