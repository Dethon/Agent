# Voice Segmented STT (Decode-Ahead) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut the latency between end-of-speech and transcript delivery — without lowering accuracy — by decoding speech at natural VAD pauses *while the user is still talking* and concatenating the segment transcripts.

**Architecture:** A new `SegmentedSpeechToText` decorator implements `ISpeechToText` and wraps the existing backend (local `WyomingSpeechToText` by default). It runs its own phrase-tuned `SilenceGate`, fires a background decode via the inner backend each time a phrase closes, and concatenates results in order. Segments are disjoint in time, so total decode work ≈ one batch decode (O(n)) — it stays viable on CPU and overlaps inference with speech. The public contract and all other backends are untouched; the feature is gated behind `Voice:Stt:Streaming:Enabled` (default off).

**Tech Stack:** .NET 10, xUnit, Shouldly. Reuses `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs` and `Domain/DTOs/Voice/*`.

**Spec:** `docs/superpowers/specs/2026-06-04-voice-segmented-stt-design.md`

**Conventions (must follow):**
- **NO trailing newline in any `.cs` file** (including tests).
- Test method names: `{Method}_{Scenario}_{ExpectedResult}`.
- File-scoped namespaces, `record` DTOs, primary constructors, LINQ over loops.
- Commit after each task (after the triplet's tests pass).

---

### Task 1: `SilenceGate.SpeechElapsed` accessor

The decorator needs to know how much *speech* sits in the un-closed tail buffer at stream end to decide drop / standalone / merge-backward. Expose the gate's existing internal counter read-only.

**Files:**
- Modify: `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`

- [ ] **Step 1: Write the failing test**

Append inside the `SilenceGateTests` class (uses the file's existing `NewGate`, `Feed`, `Loud`, `Silent` helpers; `Loud`/`Silent` are 100 ms chunks):

```csharp
[Fact]
public void SpeechElapsed_AccumulatesSpeechAndIgnoresSilence()
{
    var gate = NewGate();

    Feed(gate, Loud());   // 100 ms speech
    Feed(gate, Loud());   // 100 ms speech
    Feed(gate, Silent()); // silence — must not count

    gate.SpeechElapsed.ShouldBe(TimeSpan.FromMilliseconds(200));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~SilenceGateTests.SpeechElapsed" -v q`
Expected: FAIL — compile error, `SpeechElapsed` not defined.

- [ ] **Step 3: Add the accessor**

In `SilenceGate.cs`, add this property right after the `Decision` enum (before `Process`):

```csharp
    public TimeSpan SpeechElapsed => _speechElapsed;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~SilenceGateTests.SpeechElapsed" -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs
git commit -m "feat(voice): expose SilenceGate.SpeechElapsed for segment tail decisions"
```

---

### Task 2: `SegmentedSttConfig` settings record

**Files:**
- Modify: `McpChannelVoice/Settings/SttSettings.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSttConfigTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/Stt/SegmentedSttConfigTests.cs`:

```csharp
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class SegmentedSttConfigTests
{
    [Fact]
    public void Defaults_AreConservativeAndDisabled()
    {
        var config = new SegmentedSttConfig();

        config.Enabled.ShouldBeFalse();
        config.SilenceRmsThreshold.ShouldBe(500);
        config.SegmentSilenceMs.ShouldBe(350);
        config.MinSegmentMs.ShouldBe(800);
        config.MaxInFlightDecodes.ShouldBe(1);
        config.FinalReconcile.ShouldBeFalse();
    }

    [Fact]
    public void SttSettings_ExposesStreamingWithNonNullDefault()
    {
        new SttSettings().Streaming.ShouldNotBeNull();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSttConfigTests" -v q`
Expected: FAIL — `SegmentedSttConfig` and `SttSettings.Streaming` not defined.

- [ ] **Step 3: Add the record and property**

In `McpChannelVoice/Settings/SttSettings.cs`, add the new record at the end of the file and a `Streaming` property to `SttSettings`. The `SttSettings` record becomes:

```csharp
public record SttSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingSttConfig? Wyoming { get; init; }
    public OpenAiSttConfig? OpenAi { get; init; }
    public OpenRouterSttConfig? OpenRouter { get; init; }
    public SegmentedSttConfig Streaming { get; init; } = new();
}
```

And append this record (no trailing newline):

```csharp
public record SegmentedSttConfig
{
    public bool Enabled { get; init; }
    public double SilenceRmsThreshold { get; init; } = 500;
    public int SegmentSilenceMs { get; init; } = 350;
    public int MinSegmentMs { get; init; } = 800;
    public int MaxInFlightDecodes { get; init; } = 1;
    public bool FinalReconcile { get; init; }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSttConfigTests" -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Settings/SttSettings.cs Tests/Unit/McpChannelVoice/Stt/SegmentedSttConfigTests.cs
git commit -m "feat(voice): add SegmentedSttConfig streaming settings"
```

---

### Task 3: `SegmentedSpeechToText` — core segmentation & concatenation

Build the decorator: segment the stream at phrase pauses, decode each segment in the background (unbounded for now), concatenate in order. Backpressure, merge-backward, and fallback come in Tasks 4–6.

**Files:**
- Create: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`. The shared test harness here is reused by Tasks 4–6.

```csharp
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class SegmentedSpeechToTextTests
{
    private const int ChunkBytes = 3200; // 100 ms @ 16 kHz/16-bit/mono

    private static byte[] LoudPcm()
    {
        var pcm = new byte[ChunkBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = 0x40;     // Int16 8000 little-endian => RMS >> 500
            pcm[i + 1] = 0x1F;
        }
        return pcm;
    }

    private static AudioChunk Loud() => new()
    { Data = LoudPcm(), Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };

    private static AudioChunk Silent() => new()
    { Data = new byte[ChunkBytes], Format = AudioFormat.WyomingStandard, Timestamp = TimeSpan.Zero };

    private static IEnumerable<AudioChunk> Speech(int chunks) => Enumerable.Range(0, chunks).Select(_ => Loud());
    private static IEnumerable<AudioChunk> Silence(int chunks) => Enumerable.Range(0, chunks).Select(_ => Silent());

    private static async IAsyncEnumerable<AudioChunk> Stream(params IEnumerable<AudioChunk>[] parts)
    {
        foreach (var chunk in parts.SelectMany(p => p))
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    // 100 ms chunks: 300 ms segment-silence => 3 silent chunks close a segment;
    // 500 ms min-segment => 5 loud chunks minimum.
    private static SegmentedSttConfig Config(int maxInFlight = 1) => new()
    {
        Enabled = true,
        SilenceRmsThreshold = 500,
        SegmentSilenceMs = 300,
        MinSegmentMs = 500,
        MaxInFlightDecodes = maxInFlight
    };

    private static SegmentedSpeechToText New(ISpeechToText inner, SegmentedSttConfig? config = null) =>
        new(inner, config ?? Config(), NullLogger<SegmentedSpeechToText>.Instance);

    // Inner stub: returns the chunk count it received as text, optionally via a custom handler.
    private sealed class FakeStt(Func<int, Task<TranscriptionResult>>? handler = null) : ISpeechToText
    {
        private readonly Lock _lock = new();
        private int _concurrent;
        public int MaxConcurrent { get; private set; }
        public int Calls { get; private set; }

        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            lock (_lock) { _concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, _concurrent); Calls++; }
            try
            {
                var count = 0;
                await foreach (var _ in audio.WithCancellation(ct)) count++;
                return handler is null
                    ? new TranscriptionResult { Text = count.ToString() }
                    : await handler(count);
            }
            finally { lock (_lock) { _concurrent--; } }
        }
    }

    [Fact]
    public async Task TranscribeAsync_NoPause_DecodesWholeUtteranceOnce()
    {
        var inner = new FakeStt();

        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6)), new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(1);
        result.Text.ShouldBe("6"); // single segment of 6 chunks
    }

    [Fact]
    public async Task TranscribeAsync_PausesBetweenPhrases_ConcatenatesInOrder()
    {
        var inner = new FakeStt();

        // seg0 = 6 loud + 3 silent = 9 ; seg1 = 7 loud + 3 silent = 10 ; tail seg2 = 8 loud = 8
        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(3);
        result.Text.ShouldBe("9 10 8");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSpeechToTextTests" -v q`
Expected: FAIL — `SegmentedSpeechToText` not defined.

- [ ] **Step 3: Create the decorator (core version)**

Create `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging;

namespace McpChannelVoice.Services.Stt;

// Decodes an utterance as it streams: an internal SilenceGate tuned for phrase
// pauses slices the audio at natural silences, each closed phrase is decoded in
// the background via the inner backend, and the segment transcripts are
// concatenated in order. Segments are disjoint in time, so total decode work is
// ~one whole-utterance decode, just overlapped with speech. The single final
// TranscriptionResult is indistinguishable to callers from a batch transcript.
public sealed class SegmentedSpeechToText(
    ISpeechToText inner,
    SegmentedSttConfig config,
    ILogger<SegmentedSpeechToText> logger) : ISpeechToText
{
    private sealed record Segment(IReadOnlyList<AudioChunk> Audio, Task<TranscriptionResult> Task);

    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
    {
        var minSpeech = TimeSpan.FromMilliseconds(config.MinSegmentMs);
        var gate = new SilenceGate(
            config.SilenceRmsThreshold,
            TimeSpan.FromMilliseconds(config.SegmentSilenceMs),
            TimeSpan.MaxValue,
            minSpeech);
        var segments = new List<Segment>();
        var current = new List<AudioChunk>();

        await foreach (var chunk in audio.WithCancellation(ct))
        {
            current.Add(chunk);
            if (gate.Process(chunk.Data.Span, chunk.Format.SampleRateHz,
                    chunk.Format.SampleWidthBytes, chunk.Format.Channels) == SilenceGate.Decision.EndUtterance)
            {
                var closed = current;
                current = new List<AudioChunk>();
                gate.Reset();
                segments.Add(new Segment(closed, StartDecode(closed, options, ct)));
            }
        }

        if (gate.SpeechElapsed > TimeSpan.Zero)
        {
            segments.Add(new Segment(current, StartDecode(current, options, ct)));
        }

        if (segments.Count == 0)
        {
            return new TranscriptionResult { Text = "" };
        }

        var results = new List<TranscriptionResult>(segments.Count);
        foreach (var seg in segments)
        {
            results.Add(await seg.Task);
        }

        logger.LogInformation("Segmented STT finalized {Segments} segment(s)", segments.Count);
        return new TranscriptionResult
        {
            Text = string.Join(" ", results
                .Select(r => r.Text?.Trim())
                .Where(t => !string.IsNullOrEmpty(t))),
            Language = results.Select(r => r.Language).FirstOrDefault(l => l is not null),
            Confidence = null
        };
    }

    private Task<TranscriptionResult> StartDecode(
        IReadOnlyList<AudioChunk> chunks, TranscriptionOptions options, CancellationToken ct) =>
        Task.Run(() => inner.TranscribeAsync(ToAsyncEnumerable(chunks), options, ct), ct);

    private static async IAsyncEnumerable<AudioChunk> ToAsyncEnumerable(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSpeechToTextTests" -v q`
Expected: PASS (both tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): segmented decode-ahead STT decorator (core)"
```

---

### Task 4: Backpressure — cap concurrent decodes

On CPU, unbounded concurrent decodes would thrash. Add a `SemaphoreSlim` so at most `MaxInFlightDecodes` decode at once; extra segments queue without stalling the input loop.

**Files:**
- Modify: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `SegmentedSpeechToTextTests`:

```csharp
[Fact]
public async Task TranscribeAsync_ManySegments_RespectsMaxInFlightDecodes()
{
    // Each decode holds a slot for 50 ms so overlaps are observable.
    var inner = new FakeStt(async count =>
    {
        await Task.Delay(50);
        return new TranscriptionResult { Text = count.ToString() };
    });

    await New(inner, Config(maxInFlight: 1)).TranscribeAsync(
        Stream(Speech(6), Silence(3), Speech(7), Silence(3), Speech(8)),
        new TranscriptionOptions(), CancellationToken.None);

    inner.MaxConcurrent.ShouldBe(1);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~RespectsMaxInFlightDecodes" -v q`
Expected: FAIL — `MaxConcurrent` is 2 or 3 (decodes run unbounded).

- [ ] **Step 3: Add the semaphore**

In `SegmentedSpeechToText.cs`, create the semaphore at the top of `TranscribeAsync` (right after `var gate = ...`):

```csharp
        using var slot = new SemaphoreSlim(Math.Max(1, config.MaxInFlightDecodes));
```

Change both `StartDecode(...)` call sites to pass `slot`:

```csharp
                segments.Add(new Segment(closed, StartDecode(closed, options, slot, ct)));
```

```csharp
            segments.Add(new Segment(current, StartDecode(current, options, slot, ct)));
```

Replace `StartDecode` with the slot-aware version:

```csharp
    private Task<TranscriptionResult> StartDecode(
        IReadOnlyList<AudioChunk> chunks, TranscriptionOptions options, SemaphoreSlim slot, CancellationToken ct) =>
        Task.Run(async () =>
        {
            await slot.WaitAsync(ct);
            try
            {
                return await inner.TranscribeAsync(ToAsyncEnumerable(chunks), options, ct);
            }
            finally
            {
                slot.Release();
            }
        }, ct);
```

Note: `slot` is disposed by `using` only after all `await seg.Task` complete, so every decode has released it first.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSpeechToTextTests" -v q`
Expected: PASS (all four tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): cap concurrent segment decodes with backpressure"
```

---

### Task 5: Short final phrase — merge backward

A short phrase after a pause (e.g. a trailing "on") shouldn't be decoded alone — Whisper hallucinates on sub-second clips. If the tail has less than `MinSegmentMs` of speech and a previous segment exists, append the tail audio to that segment and re-decode the combination, replacing its result.

**Files:**
- Modify: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `SegmentedSpeechToTextTests`:

```csharp
[Fact]
public async Task TranscribeAsync_ShortFinalPhrase_MergesBackwardIntoPreviousSegment()
{
    var inner = new FakeStt();

    // seg0 = 6 loud + 3 silent = 9 ; tail = 2 loud (200 ms < 500 ms min) -> merge into seg0 => 11
    var result = await New(inner).TranscribeAsync(
        Stream(Speech(6), Silence(3), Speech(2)),
        new TranscriptionOptions(), CancellationToken.None);

    result.Text.ShouldBe("11"); // single merged segment, not "9 2"
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~MergesBackwardIntoPreviousSegment" -v q`
Expected: FAIL — result is `"9 2"` (tail decoded standalone).

- [ ] **Step 3: Add the merge-backward branch**

In `SegmentedSpeechToText.cs`, replace the tail block:

```csharp
        if (gate.SpeechElapsed > TimeSpan.Zero)
        {
            segments.Add(new Segment(current, StartDecode(current, options, slot, ct)));
        }
```

with:

```csharp
        if (gate.SpeechElapsed > TimeSpan.Zero)
        {
            if (gate.SpeechElapsed >= minSpeech || segments.Count == 0)
            {
                segments.Add(new Segment(current, StartDecode(current, options, slot, ct)));
            }
            else
            {
                var prev = segments[^1];
                ObserveAndDiscard(prev.Task);
                var merged = prev.Audio.Concat(current).ToList();
                segments[^1] = new Segment(merged, StartDecode(merged, options, slot, ct));
            }
        }
```

Add the helper (the discarded prior decode keeps running; observe it so a fault can't surface as an unobserved task exception):

```csharp
    private static void ObserveAndDiscard(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSpeechToTextTests" -v q`
Expected: PASS (all five tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): merge short final phrase backward to protect accuracy"
```

---

### Task 6: Failure fallback — whole-utterance decode

If any segment decode fails, the turn must still produce a transcript. Buffer the whole utterance and, on failure, decode it once as a single batch (today's behavior) — accuracy preserved, speed forfeited for that turn only.

**Files:**
- Modify: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `SegmentedSpeechToTextTests`:

```csharp
[Fact]
public async Task TranscribeAsync_SegmentDecodeFails_FallsBackToWholeUtterance()
{
    // seg0 = 9 chunks, tail seg1 = 7 chunks, whole = 16. Segment-sized decodes
    // throw; only the whole-utterance fallback (16 chunks) succeeds.
    var inner = new FakeStt(count => count >= 16
        ? Task.FromResult(new TranscriptionResult { Text = "whole" })
        : throw new InvalidOperationException("segment decode boom"));

    var result = await New(inner).TranscribeAsync(
        Stream(Speech(6), Silence(3), Speech(7)),
        new TranscriptionOptions(), CancellationToken.None);

    result.Text.ShouldBe("whole");
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~FallsBackToWholeUtterance" -v q`
Expected: FAIL — `InvalidOperationException` propagates out of `TranscribeAsync`.

- [ ] **Step 3: Add whole-utterance buffering and the fallback**

In `SegmentedSpeechToText.cs`, add an `all` buffer next to `current`:

```csharp
        var segments = new List<Segment>();
        var all = new List<AudioChunk>();
        var current = new List<AudioChunk>();
```

Inside the `await foreach`, record every chunk into `all` (first line of the loop body, before `current.Add`):

```csharp
            all.Add(chunk);
```

Wrap the finalization (the `results` loop, log, and `return`) in a try/catch:

```csharp
        try
        {
            var results = new List<TranscriptionResult>(segments.Count);
            foreach (var seg in segments)
            {
                results.Add(await seg.Task);
            }

            logger.LogInformation("Segmented STT finalized {Segments} segment(s)", segments.Count);
            return new TranscriptionResult
            {
                Text = string.Join(" ", results
                    .Select(r => r.Text?.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))),
                Language = results.Select(r => r.Language).FirstOrDefault(l => l is not null),
                Confidence = null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Segmented decode failed; falling back to whole-utterance decode");
            foreach (var seg in segments)
            {
                ObserveAndDiscard(seg.Task);
            }
            return await inner.TranscribeAsync(ToAsyncEnumerable(all), options, ct);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSpeechToTextTests" -v q`
Expected: PASS (all six tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): fall back to whole-utterance decode on segment failure"
```

---

### Task 7: Wire the decorator into DI + config skeleton

Wrap whichever inner backend is selected with `SegmentedSpeechToText` when `Voice:Stt:Streaming:Enabled` is true; otherwise register the inner backend directly (today's exact behavior).

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs:74-100`
- Modify: `McpChannelVoice/appsettings.json`
- Modify: `McpChannelVoice/appsettings.Development.json`
- Modify: `DockerCompose/docker-compose.yml` (mcp-channel-voice service)
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

- [ ] **Step 1: Write the failing test**

This locks the wrap/passthrough decision into a single pure helper so it's testable without a DI container. Append to `SegmentedSpeechToTextTests`:

```csharp
[Fact]
public void Wrap_WhenDisabled_ReturnsInnerUnchanged()
{
    var inner = new FakeStt();
    var result = SegmentedSpeechToText.Wrap(
        inner, new SegmentedSttConfig { Enabled = false }, NullLoggerFactory.Instance);

    result.ShouldBeSameAs(inner);
}

[Fact]
public void Wrap_WhenEnabled_ReturnsDecorator()
{
    var inner = new FakeStt();
    var result = SegmentedSpeechToText.Wrap(
        inner, new SegmentedSttConfig { Enabled = true }, NullLoggerFactory.Instance);

    result.ShouldBeOfType<SegmentedSpeechToText>();
}
```

Add `using Microsoft.Extensions.Logging.Abstractions;` (already present) — `NullLoggerFactory` lives there.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSpeechToTextTests.Wrap" -v q`
Expected: FAIL — `SegmentedSpeechToText.Wrap` not defined.

- [ ] **Step 3: Add the `Wrap` factory**

In `SegmentedSpeechToText.cs`, add `using Microsoft.Extensions.Logging;` (already imported) and this static method inside the class (above `TranscribeAsync`):

```csharp
    public static ISpeechToText Wrap(ISpeechToText inner, SegmentedSttConfig config, ILoggerFactory loggers) =>
        config.Enabled
            ? new SegmentedSpeechToText(inner, config, loggers.CreateLogger<SegmentedSpeechToText>())
            : inner;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSpeechToTextTests.Wrap" -v q`
Expected: PASS.

- [ ] **Step 5: Wire it in `ConfigModule`**

In `McpChannelVoice/Modules/ConfigModule.cs`, the STT registration currently `return`s each backend directly. Change the three `return new ...` statements to assign to a local `ISpeechToText inner`, then wrap once at the end. The lambda body becomes:

```csharp
        services.AddSingleton<ISpeechToText>(sp =>
        {
            ISpeechToText inner;
            if (settings.Stt.Provider.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                          ?? throw new InvalidOperationException("OPENAI_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
                inner = new Infrastructure.Clients.Voice.OpenAiSpeechToText(
                    http, settings.Stt.OpenAi?.Model ?? "whisper-1", key,
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenAiSpeechToText>>());
            }
            else if (settings.Stt.Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                          ?? throw new InvalidOperationException("OPENROUTER_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openrouter");
                inner = new Infrastructure.Clients.Voice.OpenRouterSpeechToText(
                    http,
                    settings.Stt.OpenRouter?.Model ?? "openai/whisper-1",
                    key,
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenRouterSpeechToText>>());
            }
            else
            {
                inner = new McpChannelVoice.Services.Stt.WyomingSpeechToText(
                    settings.Stt.Wyoming ?? throw new InvalidOperationException("Stt.Wyoming missing"),
                    sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.WyomingSpeechToText>>());
            }

            return McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, sp.GetRequiredService<ILoggerFactory>());
        });
```

- [ ] **Step 6: Add the config skeleton**

In `McpChannelVoice/appsettings.json`, inside the `"Stt"` object (after `"OpenAi"`), add:

```json
            "Streaming": {
                "Enabled": false,
                "SilenceRmsThreshold": 500,
                "SegmentSilenceMs": 350,
                "MinSegmentMs": 800,
                "MaxInFlightDecodes": 1,
                "FinalReconcile": false
            }
```

Add the same `"Streaming"` block to the `"Stt"` object in `McpChannelVoice/appsettings.Development.json` (create the `Stt` section there if it doesn't exist, mirroring `appsettings.json`).

In `DockerCompose/docker-compose.yml`, under the `mcp-channel-voice` service `environment:` list (next to `VOICE__ANNOUNCE__TOKEN`), add the per-host override placeholders (non-secret — no `.env` entry needed):

```yaml
      - VOICE__STT__STREAMING__ENABLED=false
      - VOICE__STT__STREAMING__MAXINFLIGHTDECODES=1
```

- [ ] **Step 7: Build and run the full voice test suite**

Run: `dotnet build McpChannelVoice && dotnet test Tests --filter "FullyQualifiedName~McpChannelVoice" -v q`
Expected: PASS (build succeeds; all McpChannelVoice unit tests green).

- [ ] **Step 8: Commit**

```bash
git add McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs McpChannelVoice/Modules/ConfigModule.cs McpChannelVoice/appsettings.json McpChannelVoice/appsettings.Development.json DockerCompose/docker-compose.yml Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): wire segmented STT behind Voice:Stt:Streaming flag"
```

---

### Task 8: WER measurement gate

The accuracy mandate is enforced by measurement: segmented output must not be worse than whole-utterance batch output. Add a pure word-error-rate function (unit-tested) and a corpus-driven integration test that compares both paths against reference transcripts. The corpus test skips when no recordings are present, so it is a safe no-op until the owner drops in WAV+text fixtures and points it at a reachable `wyoming-whisper`.

**Files:**
- Create: `McpChannelVoice/Services/Stt/WordErrorRate.cs`
- Test (unit): `Tests/Unit/McpChannelVoice/Stt/WordErrorRateTests.cs`
- Test (integration): `Tests/Integration/McpChannelVoice/SegmentedSttAccuracyTests.cs`

- [ ] **Step 1: Write the failing unit test**

Create `Tests/Unit/McpChannelVoice/Stt/WordErrorRateTests.cs`:

```csharp
using McpChannelVoice.Services.Stt;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class WordErrorRateTests
{
    [Fact]
    public void Compute_IdenticalText_IsZero()
    {
        WordErrorRate.Compute("enciende la luz de la cocina", "enciende la luz de la cocina")
            .ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void Compute_OneWrongWordOutOfFour_IsQuarter()
    {
        // reference 4 words, one substitution
        WordErrorRate.Compute("apaga la luz roja", "apaga la luz azul").ShouldBe(0.25, 1e-9);
    }

    [Fact]
    public void Compute_IsCaseAndPunctuationInsensitive()
    {
        WordErrorRate.Compute("Enciende la luz.", "enciende  LA luz").ShouldBe(0.0, 1e-9);
    }
}
```

- [ ] **Step 2: Run the unit test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~WordErrorRateTests" -v q`
Expected: FAIL — `WordErrorRate` not defined.

- [ ] **Step 3: Implement the WER function**

Create `McpChannelVoice/Services/Stt/WordErrorRate.cs`:

```csharp
using System.Text.RegularExpressions;

namespace McpChannelVoice.Services.Stt;

// Word-level error rate (Levenshtein edit distance over normalized word tokens)
// divided by the reference word count. Used to gate segmented STT accuracy
// against the whole-utterance baseline.
public static partial class WordErrorRate
{
    public static double Compute(string reference, string hypothesis)
    {
        var refWords = Normalize(reference);
        var hypWords = Normalize(hypothesis);
        if (refWords.Length == 0)
        {
            return hypWords.Length == 0 ? 0.0 : 1.0;
        }

        var distance = EditDistance(refWords, hypWords);
        return (double)distance / refWords.Length;
    }

    private static string[] Normalize(string text) =>
        Punctuation().Replace(text.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int EditDistance(string[] a, string[] b)
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        var curr = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]")]
    private static partial Regex Punctuation();
}
```

- [ ] **Step 4: Run the unit test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~WordErrorRateTests" -v q`
Expected: PASS.

- [ ] **Step 5: Write the corpus comparison integration test**

Create `Tests/Integration/McpChannelVoice/SegmentedSttAccuracyTests.cs`. It reads `*.wav` + matching `*.txt` (reference transcript) from a corpus directory, runs each clip through the plain inner backend and through the decorator, and asserts segmented WER does not exceed batch WER by more than a small epsilon. It skips with an explanatory assertion when the corpus is absent.

```csharp
using System.Reflection;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

// Accuracy gate. Requires a reachable wyoming-whisper (set WYOMING_WHISPER_HOST/PORT)
// and a corpus of <name>.wav + <name>.txt under SEGMENTED_STT_CORPUS. No corpus or
// no host => the test no-ops, so CI without the rig stays green.
public class SegmentedSttAccuracyTests
{
    [Fact]
    public async Task SegmentedWer_DoesNotExceedBatchWer()
    {
        var corpus = Environment.GetEnvironmentVariable("SEGMENTED_STT_CORPUS");
        var host = Environment.GetEnvironmentVariable("WYOMING_WHISPER_HOST");
        if (string.IsNullOrWhiteSpace(corpus) || !Directory.Exists(corpus) || string.IsNullOrWhiteSpace(host))
        {
            return; // rig not provisioned — skip
        }

        var port = int.TryParse(Environment.GetEnvironmentVariable("WYOMING_WHISPER_PORT"), out var p) ? p : 10300;
        var wyomingConfig = new WyomingSttConfig { Host = host, Port = port, Language = "es" };
        ISpeechToText batch = new WyomingSpeechToText(wyomingConfig, NullLogger<WyomingSpeechToText>.Instance);
        var segmented = new SegmentedSpeechToText(
            new WyomingSpeechToText(wyomingConfig, NullLogger<WyomingSpeechToText>.Instance),
            new SegmentedSttConfig { Enabled = true },
            NullLogger<SegmentedSpeechToText>.Instance);

        var clips = Directory.GetFiles(corpus, "*.wav");
        clips.Length.ShouldBeGreaterThan(0, "corpus directory has no .wav clips");

        double batchTotal = 0, segTotal = 0;
        foreach (var wav in clips)
        {
            var reference = await File.ReadAllTextAsync(Path.ChangeExtension(wav, ".txt"));
            var chunks = WavChunks.Read(wav);

            var batchText = (await batch.TranscribeAsync(Replay(chunks), new TranscriptionOptions(), default)).Text;
            var segText = (await segmented.TranscribeAsync(Replay(chunks), new TranscriptionOptions(), default)).Text;

            batchTotal += WordErrorRate.Compute(reference, batchText);
            segTotal += WordErrorRate.Compute(reference, segText);
        }

        var batchWer = batchTotal / clips.Length;
        var segWer = segTotal / clips.Length;

        // Segmented must not regress beyond a 1% absolute epsilon.
        segWer.ShouldBeLessThanOrEqualTo(batchWer + 0.01);
    }

    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }
}
```

This references a small `WavChunks.Read` helper that slices a 16 kHz/16-bit/mono WAV into 100 ms `AudioChunk`s. Create `Tests/Integration/McpChannelVoice/WavChunks.cs`:

```csharp
using Domain.DTOs.Voice;

namespace Tests.Integration.McpChannelVoice;

// Reads a 16 kHz / 16-bit / mono PCM WAV and slices it into ~100 ms AudioChunks.
internal static class WavChunks
{
    public static IReadOnlyList<AudioChunk> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var dataStart = FindDataChunk(bytes);
        const int frameBytes = 3200; // 100 ms @ 16 kHz/16-bit/mono
        var chunks = new List<AudioChunk>();
        for (var offset = dataStart; offset < bytes.Length; offset += frameBytes)
        {
            var len = Math.Min(frameBytes, bytes.Length - offset);
            chunks.Add(new AudioChunk
            {
                Data = bytes.AsMemory(offset, len),
                Format = AudioFormat.WyomingStandard,
                Timestamp = TimeSpan.Zero
            });
        }
        return chunks;
    }

    private static int FindDataChunk(byte[] bytes)
    {
        // Walk RIFF chunks from byte 12 until the "data" chunk; return its payload start.
        var i = 12;
        while (i + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, i, 4);
            var size = BitConverter.ToInt32(bytes, i + 4);
            if (id == "data")
            {
                return i + 8;
            }
            i += 8 + size;
        }
        return 44; // canonical PCM header fallback
    }
}
```

- [ ] **Step 6: Run the integration test (no-op without a rig) to verify it compiles and passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~SegmentedSttAccuracyTests" -v q`
Expected: PASS (skips cleanly — no corpus/host configured in this environment).

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Services/Stt/WordErrorRate.cs Tests/Unit/McpChannelVoice/Stt/WordErrorRateTests.cs Tests/Integration/McpChannelVoice/SegmentedSttAccuracyTests.cs Tests/Integration/McpChannelVoice/WavChunks.cs
git commit -m "feat(voice): WER accuracy gate for segmented STT"
```

---

## Final verification

- [ ] Run the full voice suite: `dotnet test Tests --filter "FullyQualifiedName~McpChannelVoice" -v q` — all green.
- [ ] Confirm `dotnet build McpChannelVoice` is clean (no warnings-as-errors from the async iterator / generated regex).
- [ ] Manual smoke (optional, requires the rig): set `Voice:Stt:Streaming:Enabled=true`, speak a multi-phrase command into a satellite, confirm the transcript matches and `SttLatencyMs` on the dashboard drops versus the disabled baseline.

## Notes for the implementer

- **Accuracy is the hard constraint.** The segmented path must never deliver a worse transcript than batch. The WER gate (Task 8) is how that is proven; do not enable `Streaming` in production until it has been run against a real Spanish corpus on the target hardware.
- **`MinSegmentMs` / `SegmentSilenceMs` are the accuracy/speed knob.** If field WER regresses, raise them (fewer, longer, more context-complete segments) before touching code.
- **Out of scope:** per-request `initial_prompt` context-carry (spike — verify stock `wyoming-whisper` support first), live partials to the LLM, `FinalReconcile` wiring (the flag exists in config but its decode path is a future enhancement).
