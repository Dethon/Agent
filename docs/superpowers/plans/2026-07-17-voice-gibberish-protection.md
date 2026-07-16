# Voice Gibberish Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the voice channel's confidence gate real (patched wyoming-whisper emits a `score` + raw whisper stats on the transcript event) and raise the bar for what audio reaches STT, so noise-triggered wakes stop producing gibberish agent turns.

**Architecture:** The hub already drops transcripts with `Confidence < ConfidenceThreshold` (`TranscriptDispatcher`), but stock wyoming-faster-whisper never emits a confidence field, so `Confidence` is always null (gate = dead code). We build a digest-pinned patched image that computes duration-weighted stats from faster-whisper's segments and attaches them as extra JSON keys on the `transcript` event (backward-compatible; hub fails open when absent). Hub-side: parse the stats, aggregate duration-weighted across streaming segments, publish them on metrics, and raise the SilenceGate entry bar (RMS 500→700, MinSpeech 200→300 ms) with per-satellite overrides.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq (`Tests/Unit`), Docker (patched `rhasspy/wyoming-whisper` image), Python (image patch overlay + verification script).

**Spec:** `docs/superpowers/specs/2026-07-17-voice-gibberish-protection-design.md`

## Global Constraints

- TDD Red-Green-Refactor per task: write the failing test, RUN it and capture the failure output, then implement. A compile error on a deliberately changed signature counts as RED.
- Commit after each task. `git add` explicit paths only (the user may commit concurrently). End commit messages with the `Claude-Session:` trailer used in this session.
- `.cs` files have **no trailing newline** (`.editorconfig`); the pre-commit hook runs `dotnet format` and re-stages whole files.
- `Domain` never references `Infrastructure`/`Agent` namespaces. `TranscriptionResult` stays a pure record.
- Prefer LINQ over loops (`.claude/rules/dotnet-style.md`). No XML doc comments; comments explain *why* only.
- Values fixed by the spec: `ConfidenceThreshold` stays **0.4**; `SilenceRmsThreshold` **500→700**; `MinSpeechMs` **200→300**; wyoming-whisper gains `--vad-threshold 0.6`. Server score formula: `score = exp(min(duration_weighted_avg_logprob, 0))`.
- No new `VoiceMetric`/`VoiceDimension` enum members are needed (NoSpeech is already observable as `FollowUpTimedOut`; new metric data are properties on `VoiceEvent`, not dimensions). If a task ever adds one anyway, append it with an explicitly pinned value — never renumber.
- Fail-open invariant: a transcript event with no `score` key must behave exactly like today (null `Confidence` passes the gate).
- Unit tests must not require Docker. The image build/verify steps (Task 8) run against the local Docker daemon.
- Test filter command shape: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~<TestClassName>"`.

---

### Task 1: `WyomingNumber.ReadDouble` tolerant parser

**Files:**
- Modify: `McpChannelVoice/Services/WyomingProtocol/WyomingNumber.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/WyomingNumberTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `internal static double? WyomingNumber.ReadDouble(JsonObject data, string key)` — returns the value for JSON numbers (int or float), `null` for missing keys, non-numeric values, NaN/Infinity. Task 2 calls it for `score`/`avg_logprob`/`no_speech_prob`/`compression_ratio`. (`InternalsVisibleTo("Tests")` already exists in `McpChannelVoice.csproj`.)

- [ ] **Step 1: Write the failing tests**

Append inside the existing `WyomingNumberTests` class (it already has the `Parse` helper):

```csharp
    [Fact]
    public void ReadDouble_FloatValue_ReturnsIt()
    {
        WyomingNumber.ReadDouble(Parse("""{"score":0.42}"""), "score").ShouldBe(0.42);
    }

    [Fact]
    public void ReadDouble_IntegerValue_ReturnsIt()
    {
        // Whisper stats are floats, but a peer may serialize a whole number as an int.
        WyomingNumber.ReadDouble(Parse("""{"score":1}"""), "score").ShouldBe(1.0);
    }

    [Fact]
    public void ReadDouble_MissingKey_ReturnsNull()
    {
        WyomingNumber.ReadDouble(Parse("""{"text":"hola"}"""), "score").ShouldBeNull();
    }

    [Fact]
    public void ReadDouble_NonNumericValue_ReturnsNull()
    {
        // A malformed score must never throw: it would surface as an STT failure and drop the turn.
        WyomingNumber.ReadDouble(Parse("""{"score":"high"}"""), "score").ShouldBeNull();
        WyomingNumber.ReadDouble(Parse("""{"score":null}"""), "score").ShouldBeNull();
        WyomingNumber.ReadDouble(Parse("""{"score":{"v":1}}"""), "score").ShouldBeNull();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingNumberTests"`
Expected: FAIL — compile error `'WyomingNumber' does not contain a definition for 'ReadDouble'`.

- [ ] **Step 3: Implement `ReadDouble`**

Append to the `WyomingNumber` class (after `ReadLong`):

```csharp
    // Stats fields (score/avg_logprob/no_speech_prob/compression_ratio) are optional quality
    // signals: absent, malformed, or non-finite values mean "no signal" (null), never an error —
    // the confidence gate fails open on null.
    public static double? ReadDouble(JsonObject data, string key)
    {
        if (data[key] is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            return d;
        }
        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }
        return null;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingNumberTests"`
Expected: PASS (all, including pre-existing `ReadInt`/`ReadLong` tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/WyomingNumber.cs Tests/Unit/McpChannelVoice/Wyoming/WyomingNumberTests.cs
git commit -m "feat(voice): tolerant double parsing for Wyoming transcript stats"
```

---

### Task 2: `TranscriptionResult` whisper stats + `WyomingSpeechToText` parse

**Files:**
- Modify: `Domain/DTOs/Voice/TranscriptionResult.cs`
- Modify: `McpChannelVoice/Services/Stt/WyomingSpeechToText.cs:70-79`
- Test: `Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs`

**Interfaces:**
- Consumes: `WyomingNumber.ReadDouble` (Task 1).
- Produces: `TranscriptionResult` gains `double? AvgLogProb`, `double? NoSpeechProb`, `double? CompressionRatio` (init-only, default null). Tasks 3 and 5 read these exact names.

- [ ] **Step 1: Write the failing tests**

Append inside `WyomingSpeechToTextTests` (reuse the fake-TCP-server pattern from the tests above them):

```csharp
    [Fact]
    public async Task TranscribeAsync_TranscriptWithStats_ParsesConfidenceAndStats()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-stop")
                {
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject
                        {
                            ["text"] = "hola mundo",
                            ["language"] = "es",
                            ["score"] = 0.83,
                            ["avg_logprob"] = -0.19,
                            ["no_speech_prob"] = 0.04,
                            ["compression_ratio"] = 1.2
                        }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        var result = await sut.TranscribeAsync(OneChunk(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        result.Text.ShouldBe("hola mundo");
        result.Confidence.ShouldBe(0.83);
        result.AvgLogProb.ShouldBe(-0.19);
        result.NoSpeechProb.ShouldBe(0.04);
        result.CompressionRatio.ShouldBe(1.2);
    }

    [Fact]
    public async Task TranscribeAsync_TranscriptWithoutStats_FailsOpenWithNulls()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-stop")
                {
                    // Stock (unpatched) server shape: text only.
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject { ["text"] = "hola" }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        var result = await sut.TranscribeAsync(OneChunk(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        result.Text.ShouldBe("hola");
        result.Confidence.ShouldBeNull();
        result.AvgLogProb.ShouldBeNull();
        result.NoSpeechProb.ShouldBeNull();
        result.CompressionRatio.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_NonNumericScore_ToleratedAsNull()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-stop")
                {
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject
                        {
                            ["text"] = "hola",
                            ["score"] = "high"
                        }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        // Previously GetValue<double>() would throw here and the whole turn would drop as SttError.
        var result = await sut.TranscribeAsync(OneChunk(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        result.Text.ShouldBe("hola");
        result.Confidence.ShouldBeNull();
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunk()
    {
        yield return new AudioChunk
        {
            Data = new byte[16],
            Format = AudioFormat.WyomingStandard,
            Timestamp = TimeSpan.Zero
        };
        await Task.Yield();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSpeechToTextTests"`
Expected: FAIL — compile error `'TranscriptionResult' does not contain a definition for 'AvgLogProb'`.

- [ ] **Step 3: Implement**

`Domain/DTOs/Voice/TranscriptionResult.cs` — full new content:

```csharp
namespace Domain.DTOs.Voice;

public record TranscriptionResult
{
    public required string Text { get; init; }
    public string? Language { get; init; }
    public double? Confidence { get; init; }

    // Raw whisper quality signals (duration-weighted per transcription by the patched
    // wyoming-whisper server). Recorded on metrics for threshold calibration; only
    // Confidence gates dispatch. Null when the server doesn't emit them (fail-open).
    public double? AvgLogProb { get; init; }
    public double? NoSpeechProb { get; init; }
    public double? CompressionRatio { get; init; }
}
```

`McpChannelVoice/Services/Stt/WyomingSpeechToText.cs` — replace the transcript-parse block (currently lines 70-79):

```csharp
            var text = evt.Data["text"]?.GetValue<string>() ?? string.Empty;
            var lang = evt.Data["language"]?.GetValue<string>();
            var score = WyomingNumber.ReadDouble(evt.Data, "score");

            logger.LogInformation("Wyoming transcript: text={Text} lang={Lang} score={Score}", text, lang, score);
            return new TranscriptionResult
            {
                Text = text,
                Language = lang,
                Confidence = score,
                AvgLogProb = WyomingNumber.ReadDouble(evt.Data, "avg_logprob"),
                NoSpeechProb = WyomingNumber.ReadDouble(evt.Data, "no_speech_prob"),
                CompressionRatio = WyomingNumber.ReadDouble(evt.Data, "compression_ratio")
            };
```

(The `double? score = null; if (evt.Data["score"] is JsonNode s) { score = s.GetValue<double>(); }` block is deleted — `ReadDouble` replaces it.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSpeechToTextTests"`
Expected: PASS (5 tests: 2 pre-existing + 3 new).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Voice/TranscriptionResult.cs McpChannelVoice/Services/Stt/WyomingSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs
git commit -m "feat(voice): parse whisper quality stats from transcript events"
```

---

### Task 3: `SegmentedSpeechToText` duration-weighted aggregation

**Files:**
- Modify: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs:99-112` (aggregation block) + new private helpers
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

**Interfaces:**
- Consumes: `TranscriptionResult.{Confidence,AvgLogProb,NoSpeechProb,CompressionRatio}` (Task 2).
- Produces: unchanged `ISpeechToText` surface; the merged `TranscriptionResult` now carries duration-weighted `Confidence`/`AvgLogProb`/`NoSpeechProb` and max `CompressionRatio` across segments.

- [ ] **Step 1: Write the failing tests**

Append inside `SegmentedSpeechToTextTests` (reuses `FakeStt`, `Stream`, `Speech`, `Silence`, `New` helpers; chunks are 100 ms each; config: 300 ms segment-silence, 500 ms min-segment):

```csharp
    [Fact]
    public async Task TranscribeAsync_SegmentsOfDifferentLengths_WeightsConfidenceByDuration()
    {
        // seg0 = 6 loud + 3 silent = 9 chunks (0.9); tail seg1 = 12 loud = 12 chunks (0.2).
        // Duration-weighted mean (9*0.9 + 12*0.2)/21 = 0.5; the old unweighted mean was 0.55.
        var inner = new FakeStt(count => Task.FromResult(
            new TranscriptionResult { Text = count.ToString(), Confidence = count == 9 ? 0.9 : 0.2 }));

        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(12)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Confidence.ShouldNotBeNull();
        result.Confidence!.Value.ShouldBe((9 * 0.9 + 12 * 0.2) / 21, 1e-9);
    }

    [Fact]
    public async Task TranscribeAsync_AggregatesWhisperStats_WeightedMeansAndMaxCompression()
    {
        var inner = new FakeStt(count => Task.FromResult(new TranscriptionResult
        {
            Text = count.ToString(),
            AvgLogProb = count == 9 ? -0.2 : -1.0,
            NoSpeechProb = count == 9 ? 0.1 : 0.7,
            CompressionRatio = count == 9 ? 1.1 : 2.9
        }));

        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(12)),
            new TranscriptionOptions(), CancellationToken.None);

        result.AvgLogProb.ShouldNotBeNull();
        result.AvgLogProb!.Value.ShouldBe((9 * -0.2 + 12 * -1.0) / 21, 1e-9);
        result.NoSpeechProb!.Value.ShouldBe((9 * 0.1 + 12 * 0.7) / 21, 1e-9);
        result.CompressionRatio.ShouldBe(2.9);
    }

    [Fact]
    public async Task TranscribeAsync_MixedConfidenceAvailability_AveragesOnlyReportingSegments()
    {
        // Fail-open composition: a segment without stats must not zero the average.
        var inner = new FakeStt(count => Task.FromResult(new TranscriptionResult
        { Text = count.ToString(), Confidence = count == 9 ? 0.6 : null }));

        var result = await New(inner).TranscribeAsync(
            Stream(Speech(6), Silence(3), Speech(12)),
            new TranscriptionOptions(), CancellationToken.None);

        result.Confidence.ShouldBe(0.6);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests"`
Expected: the two weighting tests FAIL (`0.55` instead of `0.5`; `AvgLogProb` null). The mixed-availability test may already pass — that is fine; the RED evidence is the other two.

- [ ] **Step 3: Implement**

In `SegmentedSpeechToText.TranscribeAsync`, replace the aggregation block (the `var confidences = ...` statement and the `return new TranscriptionResult { ... }` that follows it) with:

```csharp
            var weighted = segments
                .Select((seg, i) => (Weight: Math.Max(DurationSeconds(seg.Audio), 1e-9), Result: results[i]))
                .ToList();

            logger.LogInformation("Segmented STT finalized {Segments} segment(s)", segments.Count);
            return new TranscriptionResult
            {
                Text = string.Join(" ", results
                    .Select(r => r.Text?.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))),
                Language = results.Select(r => r.Language).FirstOrDefault(l => l is not null),
                Confidence = WeightedMean(weighted, r => r.Confidence),
                AvgLogProb = WeightedMean(weighted, r => r.AvgLogProb),
                NoSpeechProb = WeightedMean(weighted, r => r.NoSpeechProb),
                CompressionRatio = results.Max(r => r.CompressionRatio)
            };
```

Add two private static helpers (place after `StartDecode`):

```csharp
    // Segments differ in length, so a plain mean would let a half-second noise segment outvote
    // ten seconds of clean speech (and vice versa). Weight by audio duration; segments that
    // report no value abstain rather than dragging the average (fail-open composition).
    private static double? WeightedMean(
        IReadOnlyList<(double Weight, TranscriptionResult Result)> weighted,
        Func<TranscriptionResult, double?> selector)
    {
        var pairs = weighted
            .Where(w => selector(w.Result) is not null)
            .Select(w => (w.Weight, Value: selector(w.Result)!.Value))
            .ToList();
        return pairs.Count > 0
            ? pairs.Sum(p => p.Weight * p.Value) / pairs.Sum(p => p.Weight)
            : null;
    }

    private static double DurationSeconds(IReadOnlyList<AudioChunk> chunks) =>
        chunks.Sum(c =>
        {
            var bytesPerSecond = c.Format.SampleRateHz * c.Format.SampleWidthBytes * c.Format.Channels;
            return bytesPerSecond == 0 ? 0 : (double)c.Data.Length / bytesPerSecond;
        });
```

Note: `results.Max(r => r.CompressionRatio)` on `double?` ignores nulls and yields null when all are null — exactly the fail-open semantics we want.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests"`
Expected: PASS (all, including the pre-existing `AggregatesMean` test — equal confidences are weight-invariant).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): duration-weighted stat aggregation in segmented STT"
```

---

### Task 4: `SilenceGate.PeakRms` + `UtteranceCapture.Stats`

**Files:**
- Modify: `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`
- Modify: `McpChannelVoice/Services/UtteranceCapture.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`, `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `SilenceGate.PeakRms` (`double`, 0 until audio seen, reset by `Reset()`); `SilenceGate.SpeechElapsed` already exists.
  - `public readonly record struct CaptureStats(double PeakRms, long SpeechMs)` — declared in `UtteranceCapture.cs`, namespace `McpChannelVoice.Services`.
  - `UtteranceCapture.Stats` (`CaptureStats`) — snapshot of the gate's peak RMS and cumulative speech ms. Tasks 5-6 use these exact names.

- [ ] **Step 1: Write the failing tests**

Append inside `SilenceGateTests` (reuses `Loud()`, `Silent()`, `NewGate()`, `Feed()` helpers; `Loud()` is constant amplitude 8000 ⇒ RMS 8000):

```csharp
    [Fact]
    public void PeakRms_TracksLoudestChunkSeen()
    {
        var gate = NewGate();

        Feed(gate, Silent());
        Feed(gate, Loud());
        Feed(gate, Silent());

        gate.PeakRms.ShouldBe(8000, 1.0);
    }

    [Fact]
    public void PeakRms_Reset_ClearsIt()
    {
        var gate = NewGate();
        Feed(gate, Loud());

        gate.Reset();

        gate.PeakRms.ShouldBe(0);
    }
```

Append inside `UtteranceCaptureTests` (reuses `Loud()`, `Silent()`, `Gate()` helpers; chunks are 100 ms):

```csharp
    [Fact]
    public async Task Stats_AfterEndedCapture_ReportsPeakRmsAndSpeechMs()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.Stats.PeakRms.ShouldBe(8000, 1.0);
        capture.Stats.SpeechMs.ShouldBe(200);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests|FullyQualifiedName~UtteranceCaptureTests"`
Expected: FAIL — compile errors: `'SilenceGate' does not contain a definition for 'PeakRms'`, `'UtteranceCapture' does not contain a definition for 'Stats'`.

- [ ] **Step 3: Implement**

`SilenceGate.cs`:

1. Add a field next to the others: `private double _peakRms;`
2. Add a property next to `SpeechElapsed`: `public double PeakRms => _peakRms;`
3. In `Process`, hoist the RMS computation (currently inline in the `if`) so the peak is tracked once per chunk:

```csharp
        var rms = Rms(pcm, sampleWidthBytes);
        _peakRms = Math.Max(_peakRms, rms);

        if (rms >= rmsThreshold)
```

4. In `Reset()`, add: `_peakRms = 0;`

`UtteranceCapture.cs` — add above the `UtteranceCapture` class (below `CaptureOutcome`):

```csharp
// Audio-level facts about one capture, published on UtteranceTranscribed metrics so the
// RMS/min-speech entry bar can be tuned from real data instead of guesswork.
public readonly record struct CaptureStats(double PeakRms, long SpeechMs);
```

and add inside `UtteranceCapture` (after `Audio`):

```csharp
    public CaptureStats Stats => new(gate.PeakRms, (long)gate.SpeechElapsed.TotalMilliseconds);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests|FullyQualifiedName~UtteranceCaptureTests"`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs McpChannelVoice/Services/UtteranceCapture.cs Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs
git commit -m "feat(voice): expose capture audio stats (peak RMS, speech ms)"
```

---

### Task 5: `VoiceEvent` stat fields + `TranscriptDispatcher` publishes them

**Files:**
- Modify: `Domain/DTOs/Metrics/VoiceEvent.cs`
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs` (minimal call-site fix; Task 6 finishes it)
- Test: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`

**Interfaces:**
- Consumes: `CaptureStats` (Task 4), `TranscriptionResult` stats (Task 2).
- Produces: `TranscriptDispatcher.DispatchAsync(SatelliteSession session, TranscriptionResult transcript, string? agentId, CaptureStats? stats, CancellationToken ct)` — note the new 4th parameter. `VoiceEvent` gains `double? PeakRms`, `long? SpeechMs`, `double? AvgLogProb`, `double? NoSpeechProb`, `double? CompressionRatio`. Task 6 calls this exact signature.
- No enum changes: these are event properties, not `VoiceDimension` members.

- [ ] **Step 1: Write the failing tests**

In `TranscriptDispatcherTests`, first update every existing `DispatchAsync(...)` call (8 call sites) to pass `null` for the new `stats` parameter, e.g.:

```csharp
        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", null, default);
```

Then append two new tests:

```csharp
    [Fact]
    public async Task DispatchAsync_Dispatched_PublishesCaptureAndWhisperStats()
    {
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(Task.CompletedTask);
        var sut = new TranscriptDispatcher(
            new CapturingEmitter(), publisher.Object, manager,
            confidenceThreshold: 0.5, new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(),
            new TranscriptionResult
            {
                Text = "hola",
                Confidence = 0.8,
                AvgLogProb = -0.22,
                NoSpeechProb = 0.05,
                CompressionRatio = 1.3
            },
            "agent-1",
            new CaptureStats(PeakRms: 4200, SpeechMs: 1800),
            default);

        ok.ShouldBeTrue();
        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("dispatched");
        evt.Confidence.ShouldBe(0.8);
        evt.AvgLogProb.ShouldBe(-0.22);
        evt.NoSpeechProb.ShouldBe(0.05);
        evt.CompressionRatio.ShouldBe(1.3);
        evt.PeakRms.ShouldBe(4200);
        evt.SpeechMs.ShouldBe(1800);
    }

    [Fact]
    public async Task DispatchAsync_Dropped_PublishesCaptureAndWhisperStats()
    {
        var manager = new VoiceConversationManager(
            new Mock<IConversationFactory>().Object, new ReplyTextAccumulator(),
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            NullLogger<VoiceConversationManager>.Instance);
        var published = new List<MetricEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(Task.CompletedTask);
        var sut = new TranscriptDispatcher(
            new CapturingEmitter(), publisher.Object, manager,
            confidenceThreshold: 0.5, new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await sut.DispatchAsync(
            Session(),
            new TranscriptionResult
            {
                Text = "grbll xzzt",
                Confidence = 0.12,
                AvgLogProb = -2.1,
                NoSpeechProb = 0.55,
                CompressionRatio = 2.8
            },
            "agent-1",
            new CaptureStats(PeakRms: 900, SpeechMs: 450),
            default);

        ok.ShouldBeFalse();
        var evt = published.OfType<VoiceEvent>().Single(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        evt.Outcome.ShouldBe("dropped");
        evt.Confidence.ShouldBe(0.12);
        evt.AvgLogProb.ShouldBe(-2.1);
        evt.NoSpeechProb.ShouldBe(0.55);
        evt.CompressionRatio.ShouldBe(2.8);
        evt.PeakRms.ShouldBe(900);
        evt.SpeechMs.ShouldBe(450);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: FAIL — compile errors: no `DispatchAsync` overload with 5 arguments; `'VoiceEvent' does not contain a definition for 'PeakRms'`.

- [ ] **Step 3: Implement**

`Domain/DTOs/Metrics/VoiceEvent.cs` — append properties (after `Error`):

```csharp
    public double? PeakRms { get; init; }
    public long? SpeechMs { get; init; }
    public double? AvgLogProb { get; init; }
    public double? NoSpeechProb { get; init; }
    public double? CompressionRatio { get; init; }
```

`TranscriptDispatcher.cs`:

1. Change the signature:

```csharp
    public async Task<bool> DispatchAsync(
        SatelliteSession session,
        TranscriptionResult transcript,
        string? agentId,
        CaptureStats? stats,
        CancellationToken ct)
```

2. In **both** `VoiceEvent` initializers (dropped and dispatched), add after `Confidence = transcript.Confidence,`:

```csharp
                    AvgLogProb = transcript.AvgLogProb,
                    NoSpeechProb = transcript.NoSpeechProb,
                    CompressionRatio = transcript.CompressionRatio,
                    PeakRms = stats?.PeakRms,
                    SpeechMs = stats?.SpeechMs,
```

3. `WyomingSatelliteHost.TranscribeAndDispatchAsync` still calls the 4-arg overload and now fails to compile — patch it minimally in this task (Task 6 replaces it properly):

```csharp
            var dispatched = await dispatcher.DispatchAsync(session, result, voiceSettings.AgentId, null, ct);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: PASS (all 10: 8 updated + 2 new).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/VoiceEvent.cs McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/Services/WyomingSatelliteHost.cs Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs
git commit -m "feat(voice): publish capture + whisper stats on UtteranceTranscribed"
```

---

### Task 6: Thread the capture through `FollowUpConversation` → `WyomingSatelliteHost`

**Files:**
- Modify: `McpChannelVoice/Services/FollowUpConversation.cs:26,97`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs` (`BuildCoordinator` wiring + `TranscribeAndDispatchAsync`)
- Test: `Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs`

**Interfaces:**
- Consumes: `UtteranceCapture.{Audio,Stats}` (Task 4), `TranscriptDispatcher.DispatchAsync(..., CaptureStats?, ...)` (Task 5).
- Produces: `FollowUpConversation.TranscribeAndDispatch` is now `Func<UtteranceCapture, bool, CancellationToken, Task<bool>>` (was `Func<IAsyncEnumerable<AudioChunk>, bool, CancellationToken, Task<bool>>`). `WyomingSatelliteHost.TranscribeAndDispatchAsync(SatelliteSession, UtteranceCapture, bool, CancellationToken)`.
- Not affected: `RequestApprovalTool` calls `ISpeechToText` directly with `capture.Audio` and never goes through the dispatcher — deliberately confidence-blind (spec §2).

- [ ] **Step 1: Write the failing test**

In `FollowUpConversationTests.Harness`, record dispatched captures — add the field and change the delegate:

```csharp
        public readonly List<UtteranceCapture> Dispatched = [];
```

```csharp
            TranscribeAndDispatch = (capture, isFollowUp, _) =>
            {
                Dispatched.Add(capture);
                Events.Add(isFollowUp ? "dispatch-followup" : "dispatch-first");
                return Task.FromResult(DispatchResult);
            },
```

Append the test:

```csharp
    [Fact]
    public async Task Dispatch_ReceivesTheCaptureItOpened()
    {
        // The dispatcher needs the capture itself (audio + gate stats), not just the audio stream.
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();

        await Task.Delay(50);
        h.Dispatched.ShouldBe([h.Opened[0]]);

        await StopAsync(sut, run);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpConversationTests"`
Expected: FAIL — compile error: lambda `(capture, isFollowUp, _) => { Dispatched.Add(capture); ... }` cannot convert `IAsyncEnumerable<AudioChunk>` to `UtteranceCapture` list element (delegate still has the old type).

- [ ] **Step 3: Implement**

`FollowUpConversation.cs` — change the delegate declaration (line 26):

```csharp
    // Transcribe the captured audio and dispatch it to the agent. Receives the whole capture so
    // the dispatcher can read gate stats (peak RMS, speech ms) alongside the audio. Returns false
    // when nothing reached the agent (empty/low-confidence transcript, no session) — there will be
    // no reply, so the loop must end the conversation instead of waiting on a handshake that never
    // settles.
    public required Func<UtteranceCapture, bool, CancellationToken, Task<bool>> TranscribeAndDispatch { get; init; }
```

and the call site (line 97):

```csharp
                var dispatched = await TranscribeAndDispatch(capture, isFollowUp, ct);
```

`WyomingSatelliteHost.cs` — `BuildCoordinator` wiring:

```csharp
            TranscribeAndDispatch = (capture, isFollowUp, token) =>
                TranscribeAndDispatchAsync(session, capture, isFollowUp, token),
```

and `TranscribeAndDispatchAsync` — change signature and the two lines that used `audio`/`null`:

```csharp
    private async Task<bool> TranscribeAndDispatchAsync(
        SatelliteSession session, UtteranceCapture capture, bool isFollowUp, CancellationToken ct)
```

```csharp
            var result = await speechToText.TranscribeAsync(capture.Audio, options, ct);
```

```csharp
            var dispatched = await dispatcher.DispatchAsync(
                session, result, voiceSettings.AgentId, capture.Stats, ct);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FollowUpConversationTests"` then `dotnet build agent.sln`
Expected: PASS; solution builds clean.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/FollowUpConversation.cs McpChannelVoice/Services/WyomingSatelliteHost.cs Tests/Unit/McpChannelVoice/FollowUpConversationTests.cs
git commit -m "feat(voice): thread utterance capture stats through dispatch"
```

---

### Task 7: Per-satellite gate overrides + raised entry-bar defaults

**Files:**
- Modify: `McpChannelVoice/Settings/SatelliteConfig.cs`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs` (`BuildCoordinator.OpenCapture`)
- Modify: `McpChannelVoice/appsettings.json:6,9`
- Test: `Tests/Unit/McpChannelVoice/SatelliteConfigTests.cs`, `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs`

**Interfaces:**
- Consumes: `WyomingClientSettings` (existing).
- Produces:
  - `public record GateSettings { public double? SilenceRmsThreshold { get; init; } public int? MinSpeechMs { get; init; } }` (declared in `SatelliteConfig.cs`).
  - `SatelliteConfig.Gate` (`GateSettings?`), `SatelliteConfig.ResolveRmsThreshold(WyomingClientSettings)` (`double`), `SatelliteConfig.ResolveMinSpeechMs(WyomingClientSettings)` (`int`).
- No new env vars/secrets: `Gate` binds under the existing `Satellites` section (e.g. `Satellites__fran-office-01__Gate__SilenceRmsThreshold`), which the whole-root config binding already covers — no `docker-compose.yml`/`.env` additions required by the repo's environment-variable rule.

- [ ] **Step 1: Write the failing tests**

Append inside `SatelliteConfigTests`:

```csharp
    [Fact]
    public void ResolveGateThresholds_NoOverride_UsesGlobals()
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen" };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(700);
        config.ResolveMinSpeechMs(global).ShouldBe(300);
    }

    [Fact]
    public void ResolveGateThresholds_WithOverride_UsesSatelliteValues()
    {
        // Mic front-ends differ (e.g. XVF3800 AGC raises the noise floor), so one global RMS
        // bar can't fit every satellite.
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Gate = new GateSettings { SilenceRmsThreshold = 900, MinSpeechMs = 400 }
        };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(900);
        config.ResolveMinSpeechMs(global).ShouldBe(400);
    }

    [Fact]
    public void ResolveGateThresholds_PartialOverride_FallsBackPerField()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Gate = new GateSettings { SilenceRmsThreshold = 900 }
        };
        var global = new WyomingClientSettings { SilenceRmsThreshold = 700, MinSpeechMs = 300 };

        config.ResolveRmsThreshold(global).ShouldBe(900);
        config.ResolveMinSpeechMs(global).ShouldBe(300);
    }
```

In `VoiceSettingsBindingTests.VoiceSettings_BindsFromJson`, extend the `kitchen-01` satellite JSON:

```json
            "kitchen-01": { "Identity": "household", "Room": "Kitchen", "WakeWord": "hey_jarvis", "Address": "tcp://host.docker.internal:10800", "Gate": { "SilenceRmsThreshold": 900, "MinSpeechMs": 400 } }
```

and append assertions:

```csharp
        settings.Satellites["kitchen-01"].Gate.ShouldNotBeNull();
        settings.Satellites["kitchen-01"].Gate!.SilenceRmsThreshold.ShouldBe(900);
        settings.Satellites["kitchen-01"].Gate!.MinSpeechMs.ShouldBe(400);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteConfigTests|FullyQualifiedName~VoiceSettingsBindingTests"`
Expected: FAIL — compile error `'SatelliteConfig' does not contain a definition for 'Gate'`.

- [ ] **Step 3: Implement**

`SatelliteConfig.cs` — add inside the record (after `Tts`):

```csharp
    // Per-satellite overrides of the outer SilenceGate entry bar (WyomingClientSettings).
    // Mic front-ends sit at different noise floors (e.g. XVF3800 firmware AGC lifts quiet
    // rooms toward speech levels), so the global values can't fit every unit.
    public GateSettings? Gate { get; init; }

    public double ResolveRmsThreshold(WyomingClientSettings global) =>
        Gate?.SilenceRmsThreshold ?? global.SilenceRmsThreshold;

    public int ResolveMinSpeechMs(WyomingClientSettings global) =>
        Gate?.MinSpeechMs ?? global.MinSpeechMs;
```

and add at the bottom of the same file:

```csharp
public record GateSettings
{
    public double? SilenceRmsThreshold { get; init; }
    public int? MinSpeechMs { get; init; }
}
```

`WyomingSatelliteHost.cs` — in `BuildCoordinator`'s `OpenCapture`, replace the `SilenceGate` construction:

```csharp
                return session.OpenCapture(new SilenceGate(
                    config.ResolveRmsThreshold(settings),
                    TimeSpan.FromMilliseconds(settings.TrailingSilenceMs),
                    TimeSpan.FromMilliseconds(settings.MaxUtteranceMs),
                    TimeSpan.FromMilliseconds(config.ResolveMinSpeechMs(settings)),
                    noSpeechTimeout: TimeSpan.FromMilliseconds(followUp.WindowMs)));
```

`McpChannelVoice/appsettings.json` — raise the entry bar (spec §3):

```json
    "WyomingClient": {
        "ReconnectDelaySeconds": 5,
        "SilenceRmsThreshold": 700,
        "TrailingSilenceMs": 2000,
        "MaxUtteranceMs": 300000,
        "MinSpeechMs": 300
    },
```

(Code defaults in `WyomingClientSettings` stay 500/200, matching the existing pattern where appsettings overrides code defaults, e.g. `TrailingSilenceMs` 800→2000. The inner streaming gate `Stt.Streaming.*` is deliberately untouched.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteConfigTests|FullyQualifiedName~VoiceSettingsBindingTests"` then `dotnet build agent.sln`
Expected: PASS; clean build.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Settings/SatelliteConfig.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/SatelliteConfigTests.cs Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs
git commit -m "feat(voice): per-satellite silence-gate overrides; raise STT entry bar"
```

---

### Task 8: Patched wyoming-whisper image + compose + verification script

**Files:**
- Create: `DockerCompose/wyoming-whisper/Dockerfile`
- Create: `DockerCompose/wyoming-whisper/patch/faster_whisper_handler.py`
- Create: `DockerCompose/wyoming-whisper/patch/dispatch_handler.py`
- Create: `DockerCompose/wyoming-whisper/README.md`
- Create: `scripts/verify-whisper-score.py`
- Modify: `DockerCompose/docker-compose.yml` (wyoming-whisper service)

**Interfaces:**
- Consumes: the wire contract from Task 2 — transcript event keys `score`, `avg_logprob`, `no_speech_prob`, `compression_ratio` (JSON numbers).
- Produces: image `jackbot-wyoming-whisper` whose batch transcript events carry those keys when faster-whisper produced ≥1 segment; empty-text events (no audio / VAD-stripped / no transcriber) carry none (fail-open).

- [ ] **Step 1: Pin the upstream base and verify its version**

```bash
docker pull rhasspy/wyoming-whisper:latest
docker image inspect --format '{{index .RepoDigests 0}}' rhasspy/wyoming-whisper:latest
docker run --rm --entrypoint python3 rhasspy/wyoming-whisper:latest -c "from importlib.metadata import version; print(version('wyoming-faster-whisper'))"
```

Expected: a digest like `rhasspy/wyoming-whisper@sha256:…` (record it — the Dockerfile `FROM` uses it) and version `3.5.0`.

**Guard:** extract the live upstream sources and diff them against the v3.5.0 base this plan was written from:

```bash
mkdir -p /tmp/upstream
for f in faster_whisper_handler.py dispatch_handler.py; do
  docker run --rm --entrypoint python3 rhasspy/wyoming-whisper:latest -c "import wyoming_faster_whisper as m, pathlib; print((pathlib.Path(m.__file__).parent / '$f').read_text())" > /tmp/upstream/$f
done
```

If the version is not 3.5.0 or the extracted files differ structurally from what Step 2's patched files assume (same function bodies minus the `PATCH(jackbot)` blocks), **stop and re-derive**: take the extracted files as the base and port only the `PATCH(jackbot)`-marked changes onto them.

- [ ] **Step 2: Write the patched overlay files**

`DockerCompose/wyoming-whisper/patch/faster_whisper_handler.py` — full file (upstream v3.5.0 plus the `PATCH(jackbot)` blocks):

```python
"""Event handler for clients of the server."""

import logging
import math
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple, Union

import faster_whisper

from .const import Transcriber

_LOGGER = logging.getLogger(__name__)


class FasterWhisperTranscriber(Transcriber):
    """Event handler for clients."""

    def __init__(
        self,
        model_id: str,
        cache_dir: Union[str, Path],
        device: str = "cpu",
        compute_type: str = "default",
        cpu_threads: int = 4,
        vad_parameters: Optional[Dict[str, Any]] = None,
        task: Optional[str] = None,
    ) -> None:
        self.vad_filter = vad_parameters is not None
        self.vad_parameters = vad_parameters
        self.task = task

        self.model = faster_whisper.WhisperModel(
            model_id,
            download_root=str(cache_dir),
            device=device,
            compute_type=compute_type,
            cpu_threads=cpu_threads,
        )

    def transcribe(
        self,
        wav_path: Union[str, Path],
        language: Optional[str],
        beam_size: int = 5,
        initial_prompt: Optional[str] = None,
    ) -> Tuple[str, Optional[Dict[str, float]]]:
        # PATCH(jackbot): return (text, stats) instead of bare text so the dispatch
        # handler can attach transcription-quality stats to the transcript event.
        # dispatch_handler.py is patched in lockstep and tolerates both shapes, so
        # other backends (bare str) keep working unmodified.

        kwargs = {
            "beam_size": beam_size,
            "language": language,
            "initial_prompt": initial_prompt,
            "vad_filter": self.vad_filter,
            "vad_parameters": self.vad_parameters,
        }
        if self.task:
            kwargs["task"] = self.task

        segments, _info = self.model.transcribe(str(wav_path), **kwargs)
        # PATCH(jackbot): materialize the lazy generator so per-segment stats are readable.
        segments = list(segments)
        text = " ".join(segment.text for segment in segments)
        return text, _stats_of(segments)


# PATCH(jackbot): duration-weighted quality stats for one transcription.
# None when VAD/whisper produced zero segments (never divide by zero).
def _stats_of(segments: List[Any]) -> Optional[Dict[str, float]]:
    if not segments:
        return None

    weights = [max(s.end - s.start, 1e-6) for s in segments]
    total = sum(weights)
    avg_logprob = sum(w * s.avg_logprob for w, s in zip(weights, segments)) / total
    no_speech_prob = sum(w * s.no_speech_prob for w, s in zip(weights, segments)) / total
    return {
        # exp(mean token logprob) ~= mean token probability, in (0, 1].
        # The hub gate (VoiceSettings.ConfidenceThreshold = 0.4) sits near whisper's
        # canonical junk threshold: 0.4 ~= exp(-0.92) vs logprob_threshold = -1.0.
        "score": math.exp(min(avg_logprob, 0.0)),
        "avg_logprob": avg_logprob,
        "no_speech_prob": no_speech_prob,
        "compression_ratio": max(s.compression_ratio for s in segments),
    }
```

`DockerCompose/wyoming-whisper/patch/dispatch_handler.py` — full file (upstream v3.5.0 with two `PATCH(jackbot)` blocks in the `AudioStop` branch):

```python
"""Event handler for clients of the server."""

import asyncio
import logging
import os
import tempfile
import wave
from typing import List, Optional

from wyoming.asr import Transcribe, Transcript
from wyoming.audio import AudioChunk, AudioChunkConverter, AudioStop
from wyoming.event import Event
from wyoming.info import Describe, Info
from wyoming.server import AsyncEventHandler

from .const import StreamingSession, Transcriber
from .models import ModelLoader
from .vad import clip_wav_to_speech

_LOGGER = logging.getLogger(__name__)


class DispatchEventHandler(AsyncEventHandler):
    """Dispatches to appropriate transcriber."""

    def __init__(
        self,
        wyoming_info: Info,
        loader: ModelLoader,
        *args,
        **kwargs,
    ) -> None:
        super().__init__(*args, **kwargs)

        self.wyoming_info_event = wyoming_info.event()

        self._loader = loader
        self._transcriber: Optional[Transcriber] = None
        self._transcriber_future: Optional[asyncio.Future] = None
        self._language: Optional[str] = None

        self._wav_dir = tempfile.TemporaryDirectory()
        self._wav_path = os.path.join(self._wav_dir.name, "speech.wav")
        self._clipped_wav_path = os.path.join(self._wav_dir.name, "clipped.wav")
        self._wav_file: Optional[wave.Wave_write] = None

        self._audio_converter = AudioChunkConverter(rate=16000, width=2, channels=1)

        # Streaming state.
        # _is_streaming is None until the transcriber is loaded and the path
        # (streaming vs batch) is decided. Audio that arrives before then is
        # buffered in _pending_audio.
        self._got_audio = False
        self._is_streaming: Optional[bool] = None
        self._session: Optional[StreamingSession] = None
        self._pending_audio: List[bytes] = []

    async def handle_event(self, event: Event) -> bool:
        if AudioChunk.is_type(event.type):
            chunk = self._audio_converter.convert(AudioChunk.from_event(event))
            self._got_audio = True

            if (self._transcriber is None) and (self._transcriber_future is None):
                # Load the transcriber in the background.
                # Hopefully it's ready by the time the audio stops.
                self._transcriber_future = asyncio.create_task(
                    self._loader.load_transcriber(self._language)
                )

            # Promote the background-loaded transcriber without blocking.
            self._resolve_transcriber()

            # Decide between streaming and batch once the transcriber is ready.
            if (self._is_streaming is None) and (self._transcriber is not None):
                await self._commit_path()

            if self._is_streaming:
                assert self._session is not None
                await asyncio.to_thread(self._session.accept_chunk, chunk.audio)
            elif self._is_streaming is False:
                self._ensure_wav_file()
                assert self._wav_file is not None
                self._wav_file.writeframes(chunk.audio)
            else:
                # Transcriber not ready yet: buffer until the path is known.
                self._pending_audio.append(chunk.audio)

            return True

        if AudioStop.is_type(event.type):
            _LOGGER.debug("Audio stopped")

            # No audio was received before AudioStop — return empty transcript.
            # This happens when HA sends AudioStop without any AudioChunk
            # (e.g., VAD detected no speech, or the client disconnected early).
            if not self._got_audio:
                _LOGGER.warning("AudioStop received with no audio data")
                await self.write_event(Transcript(text="").event())
                self._reset()
                return False

            # Get the transcriber that was loading in the background.
            if self._transcriber is None:
                if self._transcriber_future is None:
                    _LOGGER.warning("No transcriber available")
                    await self.write_event(Transcript(text="").event())
                    self._reset()
                    return False
                self._transcriber = await self._transcriber_future

            # If audio arrived before the transcriber was ready, the path may
            # still be undecided — commit it now using the buffered audio.
            if self._is_streaming is None:
                await self._commit_path()

            # PATCH(jackbot): stats ride alongside the text; streaming and non-tuple
            # backends yield stats = None so their events stay byte-identical to stock.
            stats = None
            if self._is_streaming:
                assert self._session is not None
                text = await asyncio.to_thread(self._session.finish)
            else:
                assert self._wav_file is not None
                self._wav_file.close()
                self._wav_file = None

                # Optionally clip leading/trailing silence before transcription.
                # This is backend-agnostic (operates on the WAV), so it applies
                # to every batch transcriber, not just faster-whisper. Streaming
                # transcribers never reach this path.
                wav_path = self._wav_path
                if self._loader.vad_clip and await asyncio.to_thread(
                    clip_wav_to_speech,
                    self._wav_path,
                    self._clipped_wav_path,
                    self._loader.vad_clip_threshold,
                    self._loader.vad_clip_pad_ms,
                ):
                    wav_path = self._clipped_wav_path

                # Do transcription in a separate thread
                result = await asyncio.to_thread(
                    self._transcriber.transcribe,
                    wav_path,
                    self._language,
                    beam_size=self._loader.beam_size,
                    initial_prompt=self._loader.initial_prompt,
                )
                # PATCH(jackbot): the patched faster-whisper transcriber returns
                # (text, stats); other backends still return a bare string.
                if isinstance(result, tuple):
                    text, stats = result
                else:
                    text = result

            _LOGGER.info(text)

            # PATCH(jackbot): attach quality stats as extra JSON keys on the transcript
            # event. Extra keys are ignored by standard Wyoming clients (HA reads only
            # .text); the jackbot hub reads score/avg_logprob/no_speech_prob/
            # compression_ratio to drive its confidence gate.
            transcript_event = Transcript(text=text).event()
            if stats:
                transcript_event.data.update(stats)
            await self.write_event(transcript_event)
            _LOGGER.debug("Completed request")

            self._reset()

            return False

        if Transcribe.is_type(event.type):
            transcribe = Transcribe.from_event(event)
            self._language = transcribe.language or self._loader.preferred_language
            _LOGGER.debug("Language set to %s", self._language)

            return True

        if Describe.is_type(event.type):
            await self.write_event(self.wyoming_info_event)
            _LOGGER.debug("Sent info")
            return True

        return True

    def _resolve_transcriber(self) -> None:
        """Promote the background-loaded transcriber if it's ready (no blocking)."""
        if self._transcriber is not None:
            return

        future = self._transcriber_future
        if (future is not None) and future.done() and (future.exception() is None):
            self._transcriber = future.result()

    async def _commit_path(self) -> None:
        """Decide streaming vs batch and flush any buffered audio accordingly."""
        assert self._transcriber is not None

        self._is_streaming = self._transcriber.supports_streaming
        if self._is_streaming:
            self._session = self._transcriber.start_stream(
                self._language,
                beam_size=self._loader.beam_size,
                initial_prompt=self._loader.initial_prompt,
            )
            if self._pending_audio:
                # Replay audio buffered before the transcriber was ready.
                replay = b"".join(self._pending_audio)
                self._pending_audio.clear()
                await asyncio.to_thread(self._session.accept_chunk, replay)
        else:
            # Batch path: flush buffered audio to the WAV file.
            self._ensure_wav_file()
            assert self._wav_file is not None
            for buffered in self._pending_audio:
                self._wav_file.writeframes(buffered)
            self._pending_audio.clear()

    def _ensure_wav_file(self) -> None:
        """Open the temp WAV file for batch transcription if not already open."""
        if self._wav_file is None:
            self._wav_file = wave.open(self._wav_path, "wb")
            # Audio is normalized to this format by the converter.
            self._wav_file.setframerate(16000)
            self._wav_file.setsampwidth(2)
            self._wav_file.setnchannels(1)

    def _reset(self) -> None:
        """Reset per-request state."""
        if self._wav_file is not None:
            self._wav_file.close()
            self._wav_file = None

        self._language = None
        self._transcriber = None
        self._transcriber_future = None
        self._got_audio = False
        self._is_streaming = None
        self._session = None
        self._pending_audio = []
```

- [ ] **Step 3: Write the Dockerfile and README**

`DockerCompose/wyoming-whisper/Dockerfile` (substitute the digest recorded in Step 1):

```dockerfile
# Patched wyoming-faster-whisper: emits transcription-quality stats (score, avg_logprob,
# no_speech_prob, compression_ratio) as extra JSON keys on the transcript event, so the
# hub's confidence gate (TranscriptDispatcher + VoiceSettings.ConfidenceThreshold) has a
# real signal. Stock servers discard these — see README.md for the rebase procedure.
# Pinned by digest: patch/ contains full-file overlays of the 3.5.0 sources.
FROM rhasspy/wyoming-whisper@sha256:<digest-from-step-1>

COPY patch/faster_whisper_handler.py patch/dispatch_handler.py /tmp/patch/
RUN python3 -c "import pathlib, shutil, wyoming_faster_whisper as m; pkg = pathlib.Path(m.__file__).parent; [shutil.copy(f'/tmp/patch/{n}', pkg / n) for n in ('faster_whisper_handler.py', 'dispatch_handler.py')]" \
    && rm -rf /tmp/patch \
    && python3 -c "from wyoming_faster_whisper import dispatch_handler, faster_whisper_handler; print('patch applied')"
```

`DockerCompose/wyoming-whisper/README.md`:

```markdown
# Patched wyoming-whisper

Stock `wyoming-faster-whisper` discards every quality signal faster-whisper produces
(`avg_logprob`, `no_speech_prob`, `compression_ratio`) — its `Transcriber` contract
returns a bare string and the Wyoming `transcript` event carries only `text`/`language`.
The hub's confidence gate (`TranscriptDispatcher`, `VoiceSettings.ConfidenceThreshold`)
therefore never fires against a stock server.

This image overlays two files onto the digest-pinned upstream:

- `patch/faster_whisper_handler.py` — materializes the segment generator, computes
  duration-weighted `avg_logprob`/`no_speech_prob` + max `compression_ratio`, and
  returns `(text, stats)`. `score = exp(min(weighted_avg_logprob, 0))` ∈ (0, 1] —
  the hub's default threshold 0.4 ≈ whisper's canonical `logprob_threshold = -1.0`.
- `patch/dispatch_handler.py` — unpacks the tuple (tolerates bare-string backends)
  and attaches the stats as extra JSON keys on the `transcript` event. Standard
  Wyoming clients ignore extra keys; the hub reads them.

**Fail-open:** empty-audio / VAD-stripped / non-faster-whisper paths emit stock
text-only events; the hub treats a missing `score` as null confidence and passes
the transcript through — a broken patch can never kill the voice pipeline.

## Bumping the upstream image

1. `docker pull rhasspy/wyoming-whisper:latest` and note the new digest + version
   (`docker run --rm --entrypoint python3 rhasspy/wyoming-whisper:latest -c
   "from importlib.metadata import version; print(version('wyoming-faster-whisper'))"`).
2. Extract the new upstream `faster_whisper_handler.py`/`dispatch_handler.py` from the
   image and re-port the `PATCH(jackbot)` blocks onto them (they are full-file overlays).
3. Update the `FROM` digest, rebuild, and run `scripts/verify-whisper-score.py`
   (see its header for the docker invocation).
```

- [ ] **Step 4: Write the verification script**

`scripts/verify-whisper-score.py`:

```python
#!/usr/bin/env python3
"""Round-trip check for the patched wyoming-whisper score emission.

Synthesizes Spanish speech via wyoming-piper, transcribes it via wyoming-whisper, and
asserts the transcript event carries a healthy `score`. Also feeds pure noise and reports
how it fares (VAD usually strips it to an empty transcript, which is the ideal outcome).

The wyoming ports are not published to the host; run inside the compose network:

    NET=$(docker network ls --format '{{.Name}}' | grep jackbot)
    docker run --rm --network "$NET" -v "$PWD/scripts:/s:ro" python:3.12-slim \
        python /s/verify-whisper-score.py
"""
import json
import random
import socket
import sys

WHISPER = ("wyoming-whisper", 10300)
PIPER = ("wyoming-piper", 10200)
PHRASE = "Enciende la luz del salón, por favor."


def write_event(sock, etype, data=None, payload=b""):
    data_bytes = json.dumps(data or {}).encode()
    header = {
        "type": etype,
        "version": "1.0.0",
        "data_length": len(data_bytes),
        "payload_length": len(payload),
    }
    sock.sendall(json.dumps(header).encode() + b"\n" + data_bytes + payload)


def read_event(f):
    line = f.readline()
    if not line:
        return None
    header = json.loads(line)
    data = header.get("data") or {}
    if header.get("data_length"):
        data = json.loads(f.read(header["data_length"]))
    payload = f.read(header["payload_length"]) if header.get("payload_length") else b""
    return header["type"], data, payload


def synthesize(text):
    with socket.create_connection(PIPER, timeout=120) as s:
        f = s.makefile("rb")
        write_event(s, "synthesize", {"text": text})
        rate, chunks = 22050, []
        while True:
            evt = read_event(f)
            if evt is None:
                break
            etype, data, payload = evt
            if etype == "audio-start":
                rate = data.get("rate", 22050)
            elif etype == "audio-chunk":
                chunks.append(payload)
            elif etype == "audio-stop":
                break
        return rate, b"".join(chunks)


def resample_to_16k(pcm, rate):
    if rate == 16000:
        return pcm
    samples = [int.from_bytes(pcm[i:i + 2], "little", signed=True) for i in range(0, len(pcm) - 1, 2)]
    n_out = int(len(samples) * 16000 / rate)
    out = bytearray()
    for i in range(n_out):
        pos = i * (len(samples) - 1) / max(n_out - 1, 1)
        lo = int(pos)
        hi = min(lo + 1, len(samples) - 1)
        frac = pos - lo
        val = int(samples[lo] * (1 - frac) + samples[hi] * frac)
        out += max(min(val, 32767), -32768).to_bytes(2, "little", signed=True)
    return bytes(out)


def transcribe(pcm):
    fmt = {"rate": 16000, "width": 2, "channels": 1, "timestamp": 0}
    with socket.create_connection(WHISPER, timeout=300) as s:
        f = s.makefile("rb")
        write_event(s, "transcribe", {"language": "es"})
        write_event(s, "audio-start", fmt)
        for i in range(0, len(pcm), 3200):
            write_event(s, "audio-chunk", fmt, pcm[i:i + 3200])
        write_event(s, "audio-stop", {"timestamp": 0})
        while True:
            evt = read_event(f)
            if evt is None:
                return None
            etype, data, _ = evt
            if etype == "transcript":
                return data


def noise(seconds=2.0):
    rnd = random.Random(42)
    return b"".join(
        max(min(int(rnd.gauss(0, 2000)), 32767), -32768).to_bytes(2, "little", signed=True)
        for _ in range(int(16000 * seconds))
    )


def main():
    rate, speech = synthesize(PHRASE)
    if not speech:
        print("FAIL: piper returned no audio")
        sys.exit(1)
    speech_result = transcribe(resample_to_16k(speech, rate))
    print("speech:", json.dumps(speech_result, ensure_ascii=False))
    noise_result = transcribe(noise())
    print("noise: ", json.dumps(noise_result, ensure_ascii=False))

    ok = True
    if not speech_result or not speech_result.get("text", "").strip():
        print("FAIL: speech produced no transcript")
        ok = False
    elif "score" not in speech_result:
        print("FAIL: no score on speech transcript - patch not active?")
        ok = False
    elif speech_result["score"] < 0.5:
        print(f"FAIL: speech score suspiciously low ({speech_result['score']:.3f})")
        ok = False

    if noise_result and noise_result.get("text", "").strip():
        if "score" not in noise_result:
            print("FAIL: noise transcript missing score")
            ok = False
        elif noise_result["score"] >= 0.4:
            print(f"WARN: noise scored {noise_result['score']:.3f} (>= gate 0.4) - would pass")

    print("PASS" if ok else "FAIL")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
```

- [ ] **Step 5: Switch compose to the built image and add `--vad-threshold 0.6`**

In `DockerCompose/docker-compose.yml`, the `wyoming-whisper` service:

Replace:

```yaml
  wyoming-whisper:
    image: rhasspy/wyoming-whisper:latest
    container_name: wyoming-whisper
```

with:

```yaml
  wyoming-whisper:
    # Patched image: emits score/avg_logprob/no_speech_prob/compression_ratio on the
    # transcript event for the hub's confidence gate. See wyoming-whisper/README.md.
    build: ./wyoming-whisper
    image: jackbot-wyoming-whisper
    container_name: wyoming-whisper
```

Replace the VAD comment + flag block:

```yaml
      # Silero VAD trims non-speech before the decoder (fewer silence/noise hallucinations).
      # Start at the default threshold; if quiet word onsets get clipped, add `--vad-threshold`
      # below 0.5 (e.g. 0.4 then 0.3) and re-test.
      - --vad-filter
```

with:

```yaml
      # Silero VAD trims non-speech before the decoder (fewer silence/noise hallucinations).
      # Threshold raised 0.5 -> 0.6 to reject borderline noise wakes (2026-07 gibberish
      # protection). Rollback signal: quiet/far speech getting "ignored" — dispatched-utterance
      # rate dropping without a matching rise in dropped/no-speech metrics — means lower it
      # back toward 0.5 and re-test.
      - --vad-filter
      - --vad-threshold
      - "0.6"
```

- [ ] **Step 6: Build and verify**

```bash
docker build -t jackbot-wyoming-whisper DockerCompose/wyoming-whisper
```

Expected: build succeeds; last RUN prints `patch applied`. (Use plain `docker build`, not compose build — the registry cache importer breaks compose builds on this host.)

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d wyoming-whisper
NET=$(docker network ls --format '{{.Name}}' | grep jackbot)
docker run --rm --network "$NET" -v "$PWD/scripts:/s:ro" python:3.12-slim python /s/verify-whisper-score.py
```

Expected output shape:

```
speech: {"text": " Enciende la luz del salón, por favor.", "score": 0.8..., "avg_logprob": -0.2..., "no_speech_prob": 0.0..., "compression_ratio": 1...}
noise:  {"text": ""}
PASS
```

(The noise transcript being empty — VAD stripped it — is the ideal outcome; a low-scored noise transcript is also acceptable. First run may take longer while the model loads.)

- [ ] **Step 7: Commit**

```bash
git add DockerCompose/wyoming-whisper/ scripts/verify-whisper-score.py DockerCompose/docker-compose.yml
git commit -m "feat(voice): patched wyoming-whisper image emitting transcript score

Digest-pinned overlay computes duration-weighted whisper stats and attaches
score/avg_logprob/no_speech_prob/compression_ratio to the transcript event;
VAD threshold raised to 0.6. Hub fails open when the keys are absent."
```

---

### Task 9: Full suite + deployment verification

**Files:**
- No new files; runs verification across everything above.

- [ ] **Step 1: Full build and unit suite**

```bash
dotnet build agent.sln
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"
```

Expected: build clean; unit suite green (judge any failure by type — the pre-existing McpAgent cleanup integration failure is a known baseline, but it is not in the Unit filter).

- [ ] **Step 2: Rebuild and restart the hub**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build mcp-channel-voice
docker logs -f mcp-channel-voice --since 2m
```

Expected: hub starts, dials the satellite, no config-binding errors.

- [ ] **Step 3: End-to-end sanity (manual, on fran-office)**

1. Say "ok nabu, ¿qué hora es?" — normal reply; hub log line `Wyoming transcript: ... score=0.8...` (score now non-null).
2. Check the dashboard / Redis metrics: the `UtteranceTranscribed` event carries `Confidence`, `AvgLogProb`, `NoSpeechProb`, `CompressionRatio`, `PeakRms`, `SpeechMs`.
3. Optional noise probe: play a loud non-speech noise near the satellite after waking it — expect either a `NoSpeech` timeout (raised entry bar), an empty transcript, or a `dropped` UtteranceTranscribed with `Confidence < 0.4`; the agent must not answer.

- [ ] **Step 4: Commit any stragglers and wrap up**

```bash
git status --short
```

Expected: clean (everything committed per-task). If `docker` steps touched nothing else, no commit needed. Report results, including the observed speech/noise scores, back to the user.
