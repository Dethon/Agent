# Voice Turn Latency Instrumentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every span of a voice turn measurable, so wake→first-audio decomposes into named metrics with no unattributed remainder.

**Architecture:** Five new `VoiceMetric` members, all published from the voice hub (`McpChannelVoice`) using timestamps taken from the single DI `TimeProvider` that already drives `MarkTurnStart` and the playback loop. No wire-protocol change, no agent-side change, no behaviour change: every new publish goes through an existing fail-open path (`SafePublishAsync`, or the playback loop's swallow-and-log `OnFirstAudio` wrapper), so a metrics outage can never fault a turn. The Observability query service stops enumerating duration metrics by hand and derives them from the `Ms` name suffix, which also gives the voice metrics the percentiles the agent-side latency stages already have.

**Tech Stack:** .NET 10, xUnit + Shouldly, Redis Stack (metric events persist as JSON in sorted sets), Blazor WebAssembly (Dashboard).

## Global Constraints

- `.cs` files have **no trailing newline** (`.editorconfig` sets `insert_final_newline = false`).
- `VoiceMetric` / `VoiceDimension` values persist as **integers** in Redis. Only ever append; never renumber or reuse a value. New members start at 23.
- TDD is mandatory: write the failing test, run it, watch it fail, then implement.
- Commit after each task (each task is one Red-Green-Refactor triplet).
- Work on the currently checked-out branch. Never switch branches.
- The pre-commit hook runs `dotnet format` and re-stages **whole** files — make the working tree match the commit you want.
- Prefer LINQ over loops; primary constructors for DI; `record` for DTOs; no XML doc comments; comments explain *why*, never *what*.
- Metrics are diagnostic. Every new publish must be unable to fault a turn.

## Why these five spans

Measured today: `SttLatencyMs` (includes TSE), `TtsLatencyMs` (synthesis→first chunk), `WakeToFirstAudioMs` (mic-open→first chunk), plus agent-side `MemoryRecall`/`LlmTotal`/`FirstReply`.

Raw per-turn evidence from prod (2026-07-24, room "Fran's office", 73 turns; residual = `WakeToFirstAudioMs − SttLatencyMs − MemoryRecall − LlmTotal − TtsLatencyMs`, index-paired within each conversation, n = 44 turns where all five samples exist):

| | mean | p50 | p90 | max |
|---|---|---|---|---|
| `WakeToFirstAudioMs` | 15417 | 12643 | 22179 | 72854 |
| `SttLatencyMs` | 2362 | 2126 | 4292 | 8917 |
| `TtsLatencyMs` | 739 | 574 | 1115 | 3081 |
| agent `LlmTotal` | 7346 | 4045 | 16507 | 62167 |
| **unattributed residual** | **7172** | **6177** | **9867** | **35814** |
| `speechMs` (the user actually talking) | 1682 | 1520 | 2720 | 4400 |

The user speaks for ~1.5 s at the median but the residual is ~6.2 s, so roughly **4.7 s per turn is machine dead time that no metric names**. About 2 s of that is the configured trailing-silence tail; the rest is unaccounted. The remainder lives in exactly these five spans:

| New metric | Span | Currently |
|---|---|---|
| `EndpointTailMs` | last speech frame → capture close | invisible; `TrailingSilenceMs` config is ~2 s of dead air per turn |
| `SpeakerVerifyMs` | ONNX speaker verification (runs before the STT stopwatch starts) | invisible; live in prod (8 `UtteranceRejected` on 07-24) |
| `AgentRoundTripMs` | transcript dispatched → reply text back at `send_reply` | invisible; the hub cannot see agent-side stages |
| `ReplyQueueWaitMs` | reply job enqueued → synthesis starts | invisible; includes the preamble's full drain wait |
| `SpeechEndToFirstAudioMs` | capture close → first reply audio chunk | **the number that matters and does not exist**; `WakeToFirstAudioMs` starts at mic-open so it contains the user's own speech |

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` | modify | append members 23–27 |
| `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs` | modify | pin the new wire values |
| `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs` | modify | expose the trailing-silence run |
| `McpChannelVoice/Services/UtteranceCapture.cs` | modify | carry it on `CaptureStats` |
| `McpChannelVoice/Services/SatelliteSession.cs` | modify | speech-end + dispatch timestamps; `PlaybackJob.EnqueuedAt`; extend `FirstAudioTiming` |
| `McpChannelVoice/Services/WyomingSatelliteHost.cs` | modify | publish `EndpointTailMs`, `SpeakerVerifyMs`; stamp speech-end and dispatch |
| `McpChannelVoice/McpTools/SendReplyTool.cs` | modify | publish `AgentRoundTripMs`, `ReplyQueueWaitMs`, `SpeechEndToFirstAudioMs` |
| `Observability/Services/MetricsQueryService.cs` | modify | derive duration metrics from the `Ms` suffix; accept an aggregation |
| `Observability/MetricsApiEndpoints.cs` | modify | optional `agg` query param on `/voice/by/{dimension}` |
| `Dashboard.Client/Services/MetricsApiService.cs` | modify | pass `agg` through |
| `Dashboard.Client/Pages/Voice.razor` | modify | aggregation pill + the five new metric options |

---

### Task 1: Pin the five new metric members

**Files:**
- Modify: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`
- Test: `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs`

**Interfaces:**
- Produces: `VoiceMetric.EndpointTailMs` (23), `SpeakerVerifyMs` (24), `AgentRoundTripMs` (25), `ReplyQueueWaitMs` (26), `SpeechEndToFirstAudioMs` (27). Every later task publishes one of these.

- [ ] **Step 1: Write the failing test**

Append to `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs`, after `VoiceMetric_TseValues_ArePinned`:

```csharp
    [Theory]
    [InlineData(VoiceMetric.EndpointTailMs, 23)]
    [InlineData(VoiceMetric.SpeakerVerifyMs, 24)]
    [InlineData(VoiceMetric.AgentRoundTripMs, 25)]
    [InlineData(VoiceMetric.ReplyQueueWaitMs, 26)]
    [InlineData(VoiceMetric.SpeechEndToFirstAudioMs, 27)]
    public void VoiceMetric_TurnDecompositionValues_ArePinned(VoiceMetric metric, int expected)
    {
        // These five decompose wake→first-audio. Values persist as ints in Redis; a renumber
        // silently re-labels historical data.
        ((int)metric).ShouldBe(expected);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build agent.sln`
Expected: FAIL — `CS0117: 'VoiceMetric' does not contain a definition for 'EndpointTailMs'` (five such errors).

- [ ] **Step 3: Write minimal implementation**

In `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`, replace `TseLatencyMs = 22` with:

```csharp
    TseLatencyMs = 22,
    // Turn decomposition: EndpointTailMs..SpeechEndToFirstAudioMs split the wake→first-audio span
    // into the parts nothing measured before. SpeechEndToFirstAudioMs is the user-perceived one —
    // WakeToFirstAudioMs starts at mic-open, so it also contains the user's own speech.
    EndpointTailMs = 23,
    SpeakerVerifyMs = 24,
    AgentRoundTripMs = 25,
    ReplyQueueWaitMs = 26,
    SpeechEndToFirstAudioMs = 27
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/VoiceMetric.cs Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs
git commit -m "feat(voice): pin five turn-decomposition metric members"
```

---

### Task 2: `EndpointTailMs` — the endpointing tail

**Files:**
- Modify: `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`
- Modify: `McpChannelVoice/Services/UtteranceCapture.cs:16-17,43-48`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:337-345` (top of `TranscribeAndDispatchAsync`)
- Test: `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`
- Test: `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`

**Interfaces:**
- Consumes: `VoiceMetric.EndpointTailMs` from Task 1.
- Produces: `SilenceGate.TrailingSilence` (`TimeSpan`), `CaptureStats.TrailingSilenceMs` (`long`, positional param 6 with default `0`).

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`:

```csharp
    [Fact]
    public void TrailingSilence_AtEndUtterance_IsTheSilenceRunThatEndedIt()
    {
        var gate = NewGate(); // trailingSilence: 200 ms, chunks are 100 ms

        Feed(gate, Silent());
        Feed(gate, Loud());
        Feed(gate, Loud());
        Feed(gate, Silent());
        Feed(gate, Silent()).ShouldBe(SilenceGate.Decision.EndUtterance);

        gate.TrailingSilence.ShouldBe(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void TrailingSilence_ResetsWhenSpeechResumes()
    {
        var gate = NewGate();

        Feed(gate, Silent());
        Feed(gate, Loud());
        Feed(gate, Silent());
        gate.TrailingSilence.ShouldBe(TimeSpan.FromMilliseconds(100));

        Feed(gate, Loud());
        gate.TrailingSilence.ShouldBe(TimeSpan.Zero);
    }
```

Append to `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`. That file already provides `Gate(int noSpeechMs = 0)` (trailingSilence 200 ms, minSpeech 100 ms) plus `Loud()`/`Silent()`, which return 100 ms `AudioChunk`s — use them as-is:

```csharp
    [Fact]
    public void Stats_AfterTrailingSilenceEnd_CarryTheEndpointingTail()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        capture.Stats.EndReason.ShouldBe("trailing_silence");
        capture.Stats.TrailingSilenceMs.ShouldBe(200);
    }

    [Fact]
    public void Stats_MidSpeech_ReportNoEndpointingTail()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent());
        capture.Feed(Loud());

        capture.Stats.TrailingSilenceMs.ShouldBe(0);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests|FullyQualifiedName~UtteranceCaptureTests"`
Expected: FAIL — `'SilenceGate' does not contain a definition for 'TrailingSilence'` and `'CaptureStats' does not contain a definition for 'TrailingSilenceMs'`.

- [ ] **Step 3: Write minimal implementation**

In `SilenceGate.cs`, after the `TrailingRms` property:

```csharp
    // The silence run accumulated since the last speech frame. At EndUtterance this IS the
    // endpointing tail — dead air the user waits through after they stop talking — so the host can
    // publish it instead of leaving the largest unattributed span of the turn invisible.
    public TimeSpan TrailingSilence => _trailingSilence;
```

In `UtteranceCapture.cs`, extend the record struct and the `Stats` projection:

```csharp
public readonly record struct CaptureStats(
    double PeakRms, double FloorRms, long SpeechMs, string? EndReason, double TrailingRms = 0,
    long TrailingSilenceMs = 0);
```

```csharp
    public CaptureStats Stats => new(
        gate.PeakRms,
        gate.FloorRms,
        (long)gate.SpeechElapsed.TotalMilliseconds,
        _forced ? "forced" : gate.EndReason,
        gate.TrailingRms,
        (long)gate.TrailingSilence.TotalMilliseconds);
```

In `WyomingSatelliteHost.TranscribeAndDispatchAsync`, as the first statement inside the `try` (before the speaker-verification block):

```csharp
            // The endpointing tail is audio-domain time (derived from PCM frame durations), so it is
            // exact and immune to scheduling jitter. Published unconditionally — including on the
            // paths that go on to drop the transcript — because tuning TrailingSilenceMs needs the
            // rejected captures too.
            await SafePublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.EndpointTailMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = capture.Stats.TrailingSilenceMs,
                EndReason = capture.Stats.EndReason,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            });
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests|FullyQualifiedName~UtteranceCaptureTests|FullyQualifiedName~TranscriptDispatcherTests"`
Expected: PASS. `TranscriptDispatcherTests` is included because it constructs `CaptureStats` — it uses named arguments, so the new optional parameter must not break it.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs McpChannelVoice/Services/UtteranceCapture.cs McpChannelVoice/Services/WyomingSatelliteHost.cs Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs
git commit -m "feat(voice): publish EndpointTailMs so the endpointing tail is visible"
```

---

### Task 3: `SpeakerVerifyMs` — the pre-STT verification span

**Files:**
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:283-311` (`EarlyRejectAsync`) and `:345-375` (verification block in `TranscribeAndDispatchAsync`)
- Test: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`

**Interfaces:**
- Consumes: `VoiceMetric.SpeakerVerifyMs` from Task 1.
- Produces: `VoiceEvent` with `Metric = SpeakerVerifyMs`, `Outcome` = `"early"` (mid-capture check) or `"final"` (pre-STT check), `DurationMs` = elapsed ms, `Similarity` = the verdict's similarity.

- [ ] **Step 1: Write the failing test**

There is no shared fixture in `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` — each test stands up its own `TcpListener` fake satellite inline. Do **not** write a new one. Instead extend the existing test `Hub_UnknownSpeaker_RejectsCaptureWithoutSttAndPublishesMetric` (around line 384), which already runs a `RejectingVerifier` and collects into a local `List<VoiceEvent> publishedEvents`. Append these assertions next to its existing ones:

```csharp
        // Verification runs before the STT stopwatch starts, so without its own metric the ONNX
        // embedding is invisible latency. Either pass may fire depending on EarlyVerifyMs; both
        // must be timed.
        var verify = publishedEvents.Where(e => e.Metric == VoiceMetric.SpeakerVerifyMs).ToList();
        verify.ShouldNotBeEmpty();
        verify.ShouldAllBe(e => e.DurationMs != null && e.DurationMs >= 0);
        verify.ShouldAllBe(e => e.Outcome == "early" || e.Outcome == "final");
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSatelliteHostTests"`
Expected: FAIL — `verify.ShouldNotBeEmpty()` finds no `SpeakerVerifyMs` events.

- [ ] **Step 3: Write minimal implementation**

Add a private helper to `WyomingSatelliteHost`:

```csharp
    // Verification runs before the STT stopwatch starts, so without this the ONNX embedding is pure
    // invisible latency. Diagnostic only: routed through SafePublishAsync because EarlyRejectAsync
    // is awaited from the conversation loop with no catch above it.
    private Task PublishVerifyLatencyAsync(
        SatelliteSession session, long elapsedMs, double? similarity, string outcome) =>
        SafePublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.SpeakerVerifyMs,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            Outcome = outcome,
            DurationMs = elapsedMs,
            Similarity = similarity,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        });
```

In `EarlyRejectAsync`, wrap the verify call:

```csharp
        var sw = Stopwatch.StartNew();
        var verification = await speakerVerifier.VerifyAsync(
            capture.BufferedAudio, stats.SpeechMs, session.Config, ct, enforceMinSpeech: true);
        sw.Stop();
        await PublishVerifyLatencyAsync(session, sw.ElapsedMilliseconds, verification.Similarity, "early");
```

In `TranscribeAndDispatchAsync`, wrap the verify call inside `if (speakerVerifier is not null)`:

```csharp
                var verifySw = Stopwatch.StartNew();
                verification = await speakerVerifier.VerifyAsync(
                    capture.BufferedAudio, capture.Stats.SpeechMs, session.Config, ct,
                    enforceMinSpeech: !isFollowUp);
                verifySw.Stop();
                await PublishVerifyLatencyAsync(
                    session, verifySw.ElapsedMilliseconds, verification.Value.Similarity, "final");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSatelliteHostTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingSatelliteHost.cs Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs
git commit -m "feat(voice): publish SpeakerVerifyMs for both verification passes"
```

---

### Task 4: `SpeechEndToFirstAudioMs` + `ReplyQueueWaitMs`

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs:8-21` (`PlaybackJob`, `FirstAudioTiming`), `:36-44` (fields), `:126` (near `MarkTurnStart`), `:240-267` (playback loop timing)
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:249` (`CloseCapture` wiring)
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs:204-281` (`SpeakAsync`)
- Test: `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs`
- Test: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`

**Interfaces:**
- Consumes: `VoiceMetric.SpeechEndToFirstAudioMs`, `VoiceMetric.ReplyQueueWaitMs` from Task 1.
- Produces:
  - `SatelliteSession.MarkSpeechEnd(long timestamp)` — stamps capture close.
  - `PlaybackJob` gains a trailing `long EnqueuedAt = 0` parameter (`0` = unstamped).
  - `FirstAudioTiming` gains trailing `TimeSpan? SinceSpeechEnd = null, TimeSpan? QueueWait = null`. `QueueWait` ends at **synthesis start**, not first chunk, so it does not overlap `SinceSynthesisStart` (`TtsLatencyMs`).

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs`. This is `RunPlaybackLoop_FirstChunk_PublishesSynthesisAndTurnTiming` (around line 356) extended with the two new spans — copy its shape exactly, including the drain-out tail:

```csharp
    [Fact]
    public async Task RunPlaybackLoop_FirstChunk_PublishesSpeechEndAndQueueWaitTiming()
    {
        var session = MakeSession();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.MarkTurnStart(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(3));            // the user talking
        session.MarkSpeechEnd(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(2));            // verify + STT + agent
        var enqueuedAt = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(400));     // the reply waits behind the preamble

        // Synthesis takes 300 ms to produce its first chunk; 16000 bytes = 500 ms of audio.
        async IAsyncEnumerable<AudioChunk> audio()
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: audio(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; },
            EnqueuedAt: enqueuedAt);

        var pump = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);
        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Speech end -> first audio excludes the 3 s the user spent talking: 2000 + 400 + 300.
        timing.SinceSpeechEnd.ShouldBe(TimeSpan.FromMilliseconds(2700));
        // Queue wait ends at synthesis start, so it is the 400 ms behind the preamble — NOT the
        // 300 ms of synthesis, which SinceSynthesisStart already owns.
        timing.QueueWait.ShouldBe(TimeSpan.FromMilliseconds(400));
        timing.SinceSynthesisStart.ShouldBe(TimeSpan.FromMilliseconds(300));

        session.CompletePlayback();
        await Task.Delay(80);                            // let the loop reach the playback-drain wait
        time.Advance(TimeSpan.FromSeconds(1));           // drain the remaining playback duration
        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunPlaybackLoop_FirstChunk_NoSpeechEndOrEnqueueStamp_TimingsAreNull()
    {
        var session = MakeSession();
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A job with no preceding capture (chime, announce) must report nulls rather than a garbage
        // value, so the hub simply publishes nothing for those spans.
        var job = new PlaybackJob(
            Label: "chime:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("hi", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);
        session.CompletePlayback();

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceSpeechEnd.ShouldBeNull();
        timing.QueueWait.ShouldBeNull();

        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }
```

Append to `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`. That class already exposes `_session`, `_conversationId`, `_services`, `_tts` and a `List<VoiceEvent> _published` fed by a `Mock<IMetricsPublisher>` callback — assert on `_published`:

```csharp
    [Fact]
    public async Task McpRun_StreamComplete_PublishesSpeechEndAndQueueWaitMetrics()
    {
        _session.ResetTurn();
        _session.MarkTurnStart(_clock.GetTimestamp());
        _session.MarkSpeechEnd(_clock.GetTimestamp());

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var pump = _session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None, _clock);
        _session.CompletePlayback();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        var published = _published.Select(e => e.Metric).ToList();
        published.ShouldContain(VoiceMetric.SpeechEndToFirstAudioMs);
        published.ShouldContain(VoiceMetric.ReplyQueueWaitMs);
    }
```

**Test-fixture prerequisite (do this in Step 3, it is not optional):** `SendReplyTool` will resolve `TimeProvider` from the service provider, but the existing `_services` in `SendReplyToolTests` does not register one — every existing test in that class would throw. In the constructor, add a `FakeTimeProvider` field and register it:

```csharp
    private readonly Microsoft.Extensions.Time.Testing.FakeTimeProvider _clock =
        new(DateTimeOffset.UtcNow);
```

and add `.AddSingleton<TimeProvider>(_clock)` to the `_services` `ServiceCollection` chain. Production DI already registers `TimeProvider.System` (`McpChannelVoice/Modules/ConfigModule.cs:45`), so no production wiring change is needed.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionPlaybackTests|FullyQualifiedName~SendReplyToolTests"`
Expected: FAIL — `'SatelliteSession' does not contain a definition for 'MarkSpeechEnd'`, and `PlaybackJob` has no `EnqueuedAt`.

- [ ] **Step 3: Write minimal implementation**

In `SatelliteSession.cs`, extend the two records:

```csharp
public sealed record PlaybackJob(
    string Label,
    AnnouncePriority Priority,
    IAsyncEnumerable<AudioChunk> Audio,
    Func<string, Task> OnStarted,
    Func<string, Task> OnPreempted,
    Func<Task>? OnDrained = null,
    Func<FirstAudioTiming, Task>? OnFirstAudio = null,
    Func<Exception, Task>? OnFailed = null,
    long EnqueuedAt = 0);
```

```csharp
// Timing captured the moment a job's first audio chunk is produced. SinceSynthesisStart is the
// TTS time-to-first-audio (synthesis request -> first chunk); SinceTurnStart is the wake/turn-open
// -> first audio latency, null when the job had no preceding user turn. SinceSpeechEnd is the same
// span measured from capture close — the machine time the user actually waits through, with their
// own speech excluded. QueueWait ends at synthesis start, so it never overlaps SinceSynthesisStart.
public sealed record FirstAudioTiming(
    TimeSpan SinceSynthesisStart,
    TimeSpan? SinceTurnStart,
    TimeSpan? SinceSpeechEnd = null,
    TimeSpan? QueueWait = null);
```

Add the field and marker next to `_turnStartedAt` / `MarkTurnStart`:

```csharp
    private const long SpeechEndNotMarked = long.MinValue;
    private long _speechEndedAt = SpeechEndNotMarked;
```

```csharp
    // Records when the mic capture closed — the user has stopped talking, so everything after this
    // is machine time. Stamped with the same TimeProvider the playback loop reads, exactly like
    // MarkTurnStart, so the two spans are comparable.
    public void MarkSpeechEnd(long timestamp) => Interlocked.Exchange(ref _speechEndedAt, timestamp);
```

In `RunPlaybackLoopAsync`, replace the `timing` construction inside `if (chunks == 0)`:

```csharp
                            var turnStart = Interlocked.Read(ref _turnStartedAt);
                            var speechEnd = Interlocked.Read(ref _speechEndedAt);
                            var timing = new FirstAudioTiming(
                                time.GetElapsedTime(synthesisStart, firstChunkTimestamp),
                                turnStart == TurnNotStarted
                                    ? null
                                    : time.GetElapsedTime(turnStart, firstChunkTimestamp),
                                speechEnd == SpeechEndNotMarked
                                    ? null
                                    : time.GetElapsedTime(speechEnd, firstChunkTimestamp),
                                job.EnqueuedAt == 0
                                    ? null
                                    : time.GetElapsedTime(job.EnqueuedAt, synthesisStart));
```

In `WyomingSatelliteHost.BuildCoordinator`, replace `CloseCapture = session.CloseCapture,` with:

```csharp
            CloseCapture = () =>
            {
                session.CloseCapture();
                // Stamped here rather than inside the session so it uses the host's TimeProvider —
                // the same instance handed to RunPlaybackLoopAsync, which reads it back.
                session.MarkSpeechEnd(time.GetTimestamp());
            },
```

In `SendReplyTool`, thread a `TimeProvider` down and stamp the enqueue. Resolve it in `McpRun`:

```csharp
        var time = services.GetRequiredService<TimeProvider>();
```

Pass it through `HandleUtteranceReplyAsync` → `FlushAndSpeakAsync` → `SpeakAsync` (add a `TimeProvider time` parameter to each). In `SpeakAsync`, capture the enqueue timestamp and set it on the job:

```csharp
        var enqueuedAt = time.GetTimestamp();
```

```csharp
            OnFailed: _ => { if (isReply) { session.SignalTurnSilent(); } return Task.CompletedTask; },
            EnqueuedAt: enqueuedAt,
```

(`EnqueuedAt` must come after `OnFailed` in the record's parameter order; pass it as a named argument as shown so the trailing-optional ordering stays obvious.)

Extend the `OnFirstAudio` body, after the existing `WakeToFirstAudioMs` block:

```csharp
                if (timing.SinceSpeechEnd is { } sinceSpeech)
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.SpeechEndToFirstAudioMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)sinceSpeech.TotalMilliseconds,
                        ConversationId = conversationId
                    }, ct);
                }

                if (timing.QueueWait is { } queueWait)
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.ReplyQueueWaitMs,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        DurationMs = (long)queueWait.TotalMilliseconds,
                        ConversationId = conversationId
                    }, ct);
                }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"`
Expected: PASS — the whole voice suite, because `PlaybackJob`/`FirstAudioTiming` are constructed by the chime, approval and announce paths too.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/McpTools/SendReplyTool.cs Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
git commit -m "feat(voice): publish SpeechEndToFirstAudioMs and ReplyQueueWaitMs"
```

---

### Task 5: `AgentRoundTripMs` — dispatch to reply text

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs` (dispatch timestamp)
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:402-411` (after `DispatchAsync`)
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs` (`SpeakAsync`)
- Test: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`

**Interfaces:**
- Consumes: `VoiceMetric.AgentRoundTripMs` from Task 1; the `TimeProvider` threading from Task 4.
- Produces: `SatelliteSession.MarkDispatched(long timestamp)` and `SatelliteSession.DispatchedAt` (`long?`, null until stamped).

- [ ] **Step 1: Write the failing test**

Append to `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`, reusing the `_clock` registered in Task 4:

```csharp
    [Fact]
    public async Task McpRun_StreamCompleteAfterDispatch_PublishesAgentRoundTrip()
    {
        _session.ResetTurn();
        _session.MarkDispatched(_clock.GetTimestamp());
        _clock.Advance(TimeSpan.FromSeconds(4)); // the agent thinking

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        var roundTrip = _published.SingleOrDefault(e => e.Metric == VoiceMetric.AgentRoundTripMs);
        roundTrip.ShouldNotBeNull();
        roundTrip!.DurationMs.ShouldBe(4000);
    }

    [Fact]
    public async Task McpRun_StreamCompleteWithoutDispatch_PublishesNoAgentRoundTrip()
    {
        // An announce/scheduled delivery never went through a transcript dispatch, so there is no
        // round trip to report — publishing one would invent a span.
        _session.ResetTurn();

        await SendReplyTool.McpRun(_conversationId, "listo", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun(_conversationId, "", ReplyContentType.StreamComplete, true, null, _services);

        _published.ShouldNotContain(e => e.Metric == VoiceMetric.AgentRoundTripMs);
    }
```

Note: `_session` is shared per test-class instance and xUnit constructs a fresh instance per test, so `MarkDispatched` from one test cannot leak into another.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: FAIL — `'SatelliteSession' does not contain a definition for 'MarkDispatched'`.

- [ ] **Step 3: Write minimal implementation**

In `SatelliteSession.cs`, beside the speech-end field from Task 4:

```csharp
    private const long DispatchNotMarked = long.MinValue;
    private long _dispatchedAt = DispatchNotMarked;
```

```csharp
    // Stamped when a transcript actually reached the agent, so the hub can measure the agent round
    // trip it cannot otherwise see into (the agent's own MemoryRecall/LlmTotal stages live in a
    // different process). Null until the first dispatch of the connection.
    public void MarkDispatched(long timestamp) => Interlocked.Exchange(ref _dispatchedAt, timestamp);

    public long? DispatchedAt
    {
        get
        {
            var stamp = Interlocked.Read(ref _dispatchedAt);
            return stamp == DispatchNotMarked ? null : stamp;
        }
    }
```

In `WyomingSatelliteHost.TranscribeAndDispatchAsync`, inside `if (dispatched)`, as the first statement:

```csharp
                session.MarkDispatched(time.GetTimestamp());
```

In `SendReplyTool.SpeakAsync`, before building the job:

```csharp
        // Reply text arriving here closes the hub-visible agent round trip: dispatch -> answer.
        // Compared against the agent's own MemoryRecall + LlmTotal, the difference is queue time.
        if (isReply && session.DispatchedAt is { } dispatchedAt)
        {
            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.AgentRoundTripMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = (long)time.GetElapsedTime(dispatchedAt).TotalMilliseconds,
                ConversationId = conversationId
            }, ct);
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/McpTools/SendReplyTool.cs Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
git commit -m "feat(voice): publish AgentRoundTripMs from dispatch to reply text"
```

---

### Task 6: Percentiles and the new metrics on the dashboard

**Files:**
- Modify: `Observability/Services/MetricsQueryService.cs:398-429` (`GetVoiceGroupedAsync`)
- Modify: `Observability/MetricsApiEndpoints.cs:225-235`
- Modify: `Dashboard.Client/Services/MetricsApiService.cs`
- Modify: `Dashboard.Client/Pages/Voice.razor:99-111,185-191`
- Test: `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`

**Interfaces:**
- Consumes: all five members from Task 1.
- Produces: `GetVoiceGroupedAsync(VoiceDimension, VoiceMetric, DateOnly, DateOnly, LatencyMetric agg = LatencyMetric.Avg)`. Route: `GET /api/metrics/voice/by/{dimension}?metric=X&agg=P95`.

**Why the `Ms` suffix rule:** the current method hard-codes four duration metrics in a switch, so any new `…Ms` member silently reports a *count*. Deriving it from the name makes that impossible for member 28 too.

- [ ] **Step 1: Write the failing test**

Append to `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`. That class already provides `_sut` and `SetupSortedSet(key, events)`, and its existing voice tests key on `new DateOnly(2026, 3, 15)` / `"metrics:voice:2026-03-15"` — follow that exactly:

```csharp
    [Fact]
    public async Task GetVoiceGroupedAsync_NewDurationMetric_AveragesInsteadOfCounting()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:voice:2026-03-15",
        [
            new VoiceEvent { Metric = VoiceMetric.SpeechEndToFirstAudioMs, Room = "office", DurationMs = 1000 },
            new VoiceEvent { Metric = VoiceMetric.SpeechEndToFirstAudioMs, Room = "office", DurationMs = 3000 },
        ]);

        var result = await _sut.GetVoiceGroupedAsync(
            VoiceDimension.Room, VoiceMetric.SpeechEndToFirstAudioMs, date, date);

        // Regression guard: the old hard-coded switch listed only four duration metrics, so every
        // newly appended ...Ms member silently reported a count (here it would have been 2).
        result["office"].ShouldBe(2000m);
    }

    [Fact]
    public async Task GetVoiceGroupedAsync_HonoursRequestedAggregation()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:voice:2026-03-15",
        [
            new VoiceEvent { Metric = VoiceMetric.EndpointTailMs, Room = "office", DurationMs = 1000 },
            new VoiceEvent { Metric = VoiceMetric.EndpointTailMs, Room = "office", DurationMs = 9000 },
        ]);

        var result = await _sut.GetVoiceGroupedAsync(
            VoiceDimension.Room, VoiceMetric.EndpointTailMs, date, date, LatencyMetric.Max);

        result["office"].ShouldBe(9000m);
    }

    [Fact]
    public async Task GetVoiceGroupedAsync_NonDurationMetric_StillCounts()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:voice:2026-03-15",
        [
            new VoiceEvent { Metric = VoiceMetric.WakeTriggered, Room = "office" },
            new VoiceEvent { Metric = VoiceMetric.WakeTriggered, Room = "office" },
        ]);

        var result = await _sut.GetVoiceGroupedAsync(
            VoiceDimension.Room, VoiceMetric.WakeTriggered, date, date);

        result["office"].ShouldBe(2m);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests"`
Expected: FAIL — the first test returns `2` (a count, not the 2000 ms mean); the second does not compile (no `agg` parameter).

- [ ] **Step 3: Write minimal implementation**

Replace the tail of `GetVoiceGroupedAsync`:

```csharp
    public async Task<Dictionary<string, decimal>> GetVoiceGroupedAsync(
        VoiceDimension dimension,
        VoiceMetric metric,
        DateOnly from,
        DateOnly to,
        LatencyMetric agg = LatencyMetric.Avg)
    {
```

```csharp
        // Duration metrics are identified by their name suffix rather than an explicit list: the
        // list silently degraded every newly added ...Ms member to a count.
        var isDuration = metric.ToString().EndsWith("Ms", StringComparison.Ordinal);

        return scoped
            .GroupBy(e => selector(e) ?? "(unknown)")
            .ToDictionary(
                g => g.Key,
                g => isDuration
                    ? AggregateLatency(g.Select(e => (decimal)(e.DurationMs ?? 0)), agg)
                    : (decimal)g.Count());
```

In `MetricsApiEndpoints.cs`, add the parameter to the `/voice/by/{dimension}` handler:

```csharp
        api.MapGet("/voice/by/{dimension}", async (
            MetricsQueryService query,
            VoiceDimension dimension,
            VoiceMetric metric,
            LatencyMetric? agg,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetVoiceGroupedAsync(
                dimension, metric, fromDate, toDate, agg ?? LatencyMetric.Avg);
        });
```

In `Dashboard.Client/Services/MetricsApiService.cs`, replace `GetVoiceGroupedAsync`:

```csharp
    public Task<Dictionary<string, decimal>?> GetVoiceGroupedAsync(
        VoiceDimension dimension, VoiceMetric metric, DateOnly from, DateOnly to,
        LatencyMetric agg = LatencyMetric.Avg, CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/voice/by/{dimension}?metric={metric}&agg={agg}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);
```

In `Dashboard.Client/Pages/Voice.razor`, add the five new entries to `MetricOptions` (put the headline first so it is the obvious pick):

```csharp
        new("Speech end → audio (ms)", nameof(VoiceMetric.SpeechEndToFirstAudioMs)),
        new("Endpoint tail (ms)", nameof(VoiceMetric.EndpointTailMs)),
        new("Speaker verify (ms)", nameof(VoiceMetric.SpeakerVerifyMs)),
        new("Agent round trip (ms)", nameof(VoiceMetric.AgentRoundTripMs)),
        new("Reply queue wait (ms)", nameof(VoiceMetric.ReplyQueueWaitMs)),
```

Replace `GetMetricUnit()` so it cannot go stale as more `…Ms` members are appended:

```csharp
    // Mirrors the query service: any metric whose name ends in Ms is a duration.
    private string GetMetricUnit() =>
        _state.Metric.ToString().EndsWith("Ms", StringComparison.Ordinal) ? "ms" : "";
```

Add the aggregation options list beside `TimeOptions`:

```csharp
    private static readonly IReadOnlyList<PillOption> AggOptions =
    [
        new("Avg", nameof(LatencyMetric.Avg)),
        new("P50", nameof(LatencyMetric.P50)),
        new("P95", nameof(LatencyMetric.P95)),
        new("Max", nameof(LatencyMetric.Max)),
    ];
```

Add the field beside `_selectedDays`:

```csharp
    private LatencyMetric _agg = LatencyMetric.Avg;
```

Add the pill to the `controls` div, after the Metric selector:

```razor
            <PillSelector Label="Aggregate" Options="AggOptions" Value="@_agg.ToString()"
                          OnChanged="OnAggChanged" />
```

Add the handler beside `OnMetricChanged`:

```csharp
    private async Task OnAggChanged(string v)
    {
        _agg = Enum.Parse<LatencyMetric>(v);
        await Storage.SetAsync("voice.agg", v);
        await ReloadBreakdown();
    }
```

Restore it in `OnInitializedAsync`, beside the other `Storage.GetAsync` calls:

```csharp
        var savedAgg = await Storage.GetAsync<LatencyMetric>("voice.agg");
```
```csharp
        if (savedAgg.HasValue) _agg = savedAgg.Value;
```

Pass it through in `ReloadBreakdown`:

```csharp
        var breakdown = await Api.GetVoiceGroupedAsync(
            Store.State.GroupBy, Store.State.Metric, _from, _to, _agg);
```

Finally, retarget the latency KPI card at the metric that actually represents waiting. In the markup:

```razor
        <KpiCard Label="Median speech → audio" Value="@($"{_medianLatency:F0}ms")" Color="var(--accent-yellow)" />
```

and in the `StateObservable` subscription:

```csharp
            // WakeToFirstAudioMs starts at mic-open, so its median is inflated by however long the
            // user spoke. SpeechEndToFirstAudioMs is the wait. Wake → audio stays available in the
            // chart's metric selector.
            var lat = s.Events.Where(e => e.Metric == VoiceMetric.SpeechEndToFirstAudioMs && e.DurationMs is not null)
                              .Select(e => e.DurationMs!.Value).OrderBy(x => x).ToList();
```

Add `@using Domain.DTOs.Metrics.Enums` if it is not already at the top of the file (it is — line 4).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests"` then `dotnet build agent.sln`
Expected: PASS, clean build.

- [ ] **Step 5: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Observability/MetricsApiEndpoints.cs Dashboard.Client/Services/MetricsApiService.cs Dashboard.Client/Pages/Voice.razor Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs
git commit -m "feat(observability): percentiles for voice duration metrics and the new turn spans"
```

---

## Verification after Task 6

- [ ] `dotnet build agent.sln` — clean.
- [ ] `dotnet test Tests/Tests.csproj --filter "Category!=E2E"` — no new failures (the McpAgent cleanup test fails pre-existing; judge by failure type, not count).
- [ ] Rebuild and restart `mcp-channel-voice` and `observability`, speak one turn, then confirm the decomposition closes:
      `curl -s "http://192.168.5.45:5003/api/metrics/voice/by/Room?metric=SpeechEndToFirstAudioMs&agg=P50&from=<today>&to=<today>"`
      and check `EndpointTailMs + SpeakerVerifyMs + SttLatencyMs + AgentRoundTripMs + ReplyQueueWaitMs + TtsLatencyMs ≈ SpeechEndToFirstAudioMs` for that turn.

## Follow-on plans (deliberately out of scope here)

Both depend on Phase 1's numbers, so they get their own plans once a day of data has landed:

1. **Stream the reply.** `SendReplyTool` buffers all text and synthesizes only at `StreamComplete` (`SendReplyTool.cs:106-107`), so voice pays `LlmTotal` (2026-07-24 avg 7346 ms, P50 4045, P95 28060) rather than `FirstReply` (avg 5741, P50 3445). Sentence-chunked synthesis is the largest single win available. It is also the riskiest change in this area — the per-turn `SignalTurnSpoken` handshake must fire exactly once, on the last sentence, and the time-to-first-audio metrics must stay anchored to the first sentence only (mirror the `TryClaimPreamble` idiom). Known adjacent hazard: the deferred turn-handshake races (late reply / approval vs the 120 s reply timeout).
2. **Segmented STT.** `SegmentedSpeechToText` exists to overlap decoding with speech, but `TranscribeAndDispatch` runs only after the capture has fully closed (`FollowUpConversation.cs:96-116`) and `MaxInFlightDecodes` is `1`, so it delivers neither overlap nor parallelism — it just multiplies whisper's per-request overhead. True overlap conflicts with both TSE (needs the whole mixture) and the speaker gate (needs the whole buffered capture), so the decision is evidence-driven: add a per-turn segment-count metric, then either raise `MaxInFlightDecodes` or disable segmentation. Reference point: ~876 ms warm p50 for a single whole-utterance large-turbo decode on this box, against a measured `SttLatencyMs` of 2362 ms.
