# TSE Live Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deploy round-1's winning TSE model into the live voice pipeline as a fail-open, config-gated STT-path decorator plus a `tse-extractor` sidecar container, instrumented for a live trial on the pi5 stack.

**Architecture:** A `TseSpeechToText` decorator wraps the existing `ISpeechToText` chain in `McpChannelVoice`; the host passes the speaker gate's verdict and the capture's pre-speech floor through two new `TranscriptionOptions` fields; extraction runs in a new Python sidecar (WeSep BSRNN + ECAPA loaded once, enrollment cached per speaker from the prod `voices/` volume). Spec: `docs/superpowers/specs/2026-07-22-tse-live-integration-design.md`.

**Tech Stack:** .NET 10 (hub decorator, xunit + Shouldly tests), Python 3.11 + Flask + wesep/wespeaker (sidecar container), Docker Compose.

## Global Constraints

- Branch: all commits on the currently checked-out `noise`. Never switch branches.
- `.cs` files have **no trailing newline** (`.editorconfig` `insert_final_newline = false`) — applies to Tests too. The pre-commit hook re-formats and re-stages whole files.
- TDD for all hub code: failing test first, watch it fail, implement, watch it pass.
- Fail-open everywhere: any TSE unavailability (mode Off, no target speaker, quiet floor, HTTP error, timeout, 404) must leave the inner STT receiving the raw audio, byte-identical to today.
- Gate and endpointing keep consuming **raw** audio — this plan must not touch `SilenceGate`, `UtteranceCapture`, `SpeakerVerifier`, or any satellite code.
- `VoiceMetric` values are persisted as ints: append with explicitly pinned numbers (next free = 19), never renumber (see the enum's header comment).
- New env/config keys land **in the same commit** as the code that reads them: `DockerCompose/docker-compose.yml` + `McpChannelVoice/appsettings.json` (non-secret config — nothing goes to `.env`).
- Audio interchange: 16 kHz mono S16LE (`AudioFormat.WyomingStandard`).
- Sidecar checkpoint identity (must match round 1): WeSep `bsrnn_ecapa_vox1` from `https://www.modelscope.cn/datasets/wenet/wesep_pretrained_models/resolve/master/bsrnn_ecapa_vox1.tar.gz`; wespeaker commit `e9bbf73d0fd13db6cf42a6cb2eafb0d7dd0f8e0e`; wesep commit `99eca54b60300d39b9353d93cf285a14bba37854`.
- Build/test commands: `dotnet build agent.sln` and `dotnet test Tests/Tests.csproj --filter "<name>"` from the repo root. Serialize builds (one owner per checkout — concurrent dotnet runs livelock WSL).

---

### Task 1: `TranscriptionOptions` fields + `TranscriptionOptionsFactory` + host wiring

**Files:**
- Modify: `Domain/DTOs/Voice/TranscriptionOptions.cs`
- Create: `McpChannelVoice/Services/Stt/TranscriptionOptionsFactory.cs`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs` (inside `TranscribeAndDispatchAsync`, currently ~lines 330-375)
- Test: `Tests/Unit/McpChannelVoice/TranscriptionOptionsFactoryTests.cs`

**Interfaces:**
- Consumes: `SpeakerVerification` (readonly record struct: `Decision`, `Similarity`, `BestMatch`, `IdentifiedSpeaker`) from `McpChannelVoice.Services.Verification`; `CaptureStats` (`PeakRms, FloorRms, SpeechMs, EndReason, TrailingRms`) from `McpChannelVoice.Services`; `SatelliteConfig` (has `Stt?.Wyoming?.Language`).
- Produces: `TranscriptionOptions.TargetSpeaker` (`string?`) and `TranscriptionOptions.NoiseFloorRms` (`double?`); `TranscriptionOptionsFactory.Create(SatelliteConfig config, SpeakerVerification? verification, CaptureStats stats) -> TranscriptionOptions`. Task 7's decorator reads exactly these two option fields.

- [ ] **Step 1: Write the failing test**

`Tests/Unit/McpChannelVoice/TranscriptionOptionsFactoryTests.cs` (mirror the namespace style of the existing files in that folder, e.g. `namespace Tests.Unit.McpChannelVoice;`):
```csharp
using McpChannelVoice.Services;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TranscriptionOptionsFactoryTests
{
    private static readonly CaptureStats Stats = new(PeakRms: 2000, FloorRms: 512.5, SpeechMs: 1500, EndReason: "ended");

    [Fact]
    public void ConclusiveIdentityWins()
    {
        var v = new SpeakerVerification(SpeakerDecision.Accepted, 0.81, BestMatch: "Tradaly", IdentifiedSpeaker: "Dethon");
        var options = TranscriptionOptionsFactory.Create(new SatelliteConfig(), v, Stats);
        options.TargetSpeaker.ShouldBe("Dethon");
        options.NoiseFloorRms.ShouldBe(512.5);
    }

    [Fact]
    public void AcceptedButAmbiguousFallsBackToBestMatch()
    {
        var v = new SpeakerVerification(SpeakerDecision.Accepted, 0.66, BestMatch: "Tradaly", IdentifiedSpeaker: null);
        TranscriptionOptionsFactory.Create(new SatelliteConfig(), v, Stats).TargetSpeaker.ShouldBe("Tradaly");
    }

    [Theory]
    [InlineData(SpeakerDecision.Rejected)]
    [InlineData(SpeakerDecision.Skipped)]
    [InlineData(SpeakerDecision.Unavailable)]
    public void NonAcceptedDecisionsYieldNoTarget(SpeakerDecision decision)
    {
        var v = new SpeakerVerification(decision, BestMatch: "Dethon", IdentifiedSpeaker: "Dethon");
        TranscriptionOptionsFactory.Create(new SatelliteConfig(), v, Stats).TargetSpeaker.ShouldBeNull();
    }

    [Fact]
    public void NoVerifierMeansNoTargetButFloorStillFlows()
    {
        var options = TranscriptionOptionsFactory.Create(new SatelliteConfig(), verification: null, Stats);
        options.TargetSpeaker.ShouldBeNull();
        options.NoiseFloorRms.ShouldBe(512.5);
    }
}
```
Note: check `SatelliteConfig`'s constructor before writing — if it has required members, instantiate the minimal valid form used by the existing tests in `Tests/Unit/McpChannelVoice/` (grep for `new SatelliteConfig` there and copy the pattern). If a `Language` is configured via `config.Stt.Wyoming.Language`, add one assertion that it flows through to `options.Language`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptionOptionsFactoryTests"`
Expected: compile FAIL (`TranscriptionOptionsFactory` does not exist; `TargetSpeaker` not a member).

- [ ] **Step 3: Implement**

`Domain/DTOs/Voice/TranscriptionOptions.cs` — add two members to the existing record:
```csharp
namespace Domain.DTOs.Voice;

public record TranscriptionOptions
{
    public string? Language { get; init; }
    public string? ModelHint { get; init; }
    public TimeSpan? Timeout { get; init; }
    // Target-speaker-extraction hints, set by the voice host from the speaker gate's verdict and
    // the capture's frozen pre-speech floor; consumed only by TseSpeechToText. Null TargetSpeaker
    // means extraction cannot run for this call.
    public string? TargetSpeaker { get; init; }
    public double? NoiseFloorRms { get; init; }
}
```

`McpChannelVoice/Services/Stt/TranscriptionOptionsFactory.cs`:
```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

public static class TranscriptionOptionsFactory
{
    // TargetSpeaker: the gate's conclusive identity when present, else the accepted top-1
    // (BestMatch). Any non-Accepted decision leaves it null — extraction has no reliable
    // target for skipped/unavailable verifications, and rejected captures never reach STT.
    public static TranscriptionOptions Create(
        SatelliteConfig config, SpeakerVerification? verification, CaptureStats stats) =>
        new()
        {
            Language = config.Stt?.Wyoming?.Language,
            TargetSpeaker = verification is { Decision: SpeakerDecision.Accepted } v
                ? v.IdentifiedSpeaker ?? v.BestMatch
                : null,
            NoiseFloorRms = stats.FloorRms
        };
}
```

`McpChannelVoice/Services/WyomingSatelliteHost.cs`, in `TranscribeAndDispatchAsync`: hoist the verification into a nullable local so the factory sees it, and replace the inline options construction. The current code is:
```csharp
            double? similarity = null;
            string? identifiedSpeaker = null;
            if (speakerVerifier is not null)
            {
                var verification = await speakerVerifier.VerifyAsync(
```
change to:
```csharp
            double? similarity = null;
            string? identifiedSpeaker = null;
            SpeakerVerification? verification = null;
            if (speakerVerifier is not null)
            {
                verification = await speakerVerifier.VerifyAsync(
```
and update the in-block reads to `verification.Value.…` (`Decision`, `Similarity`, `IdentifiedSpeaker`). Then replace:
```csharp
            var options = new TranscriptionOptions { Language = session.Config.Stt?.Wyoming?.Language };
```
with:
```csharp
            var options = TranscriptionOptionsFactory.Create(session.Config, verification, capture.Stats);
```
(keep the explanatory comment above it about the per-satellite language override — it still applies via the factory).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptionOptionsFactoryTests"`
Expected: PASS (5-6 tests). Then `dotnet build agent.sln` — clean build (host compiles with the hoisted nullable).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Voice/TranscriptionOptions.cs McpChannelVoice/Services/Stt/TranscriptionOptionsFactory.cs McpChannelVoice/Services/WyomingSatelliteHost.cs Tests/Unit/McpChannelVoice/TranscriptionOptionsFactoryTests.cs
git commit -m "feat(voice): thread speaker-gate verdict and noise floor into TranscriptionOptions"
```

---

### Task 2: `TseSettings` + config/infra skeletons

**Files:**
- Create: `McpChannelVoice/Settings/TseSettings.cs`
- Modify: `McpChannelVoice/Settings/VoiceSettings.cs` (add one property)
- Modify: `McpChannelVoice/appsettings.json`
- Modify: `DockerCompose/docker-compose.yml` (`mcp-channel-voice` service: env keys + audit volume)

**Interfaces:**
- Produces: `TseMode { Off, Auto, Always }` and `TseSettings { TseMode Mode = Off; string Endpoint = "http://tse-extractor:9098"; int TimeoutMs = 90000; double NoiseFloorThreshold = 400; string? AuditDir = null; int AuditMaxPairs = 50 }`, reachable as `VoiceSettings.Tse`. Tasks 5-8 consume these names verbatim.

- [ ] **Step 1: Create the settings record**

`McpChannelVoice/Settings/TseSettings.cs`:
```csharp
namespace McpChannelVoice.Settings;

public enum TseMode
{
    Off,
    Auto,
    Always
}

// Target-speaker extraction (spec: docs/superpowers/specs/2026-07-22-tse-live-integration-design.md).
// Mode is the kill switch: Off = decorator not even wrapped (restart to change). Auto extracts only
// when the gate produced a target AND the capture's pre-speech floor is at/above
// NoiseFloorThreshold; Always extracts whenever a target exists (diagnostic).
public record TseSettings
{
    public TseMode Mode { get; init; } = TseMode.Off;
    public string Endpoint { get; init; } = "http://tse-extractor:9098";
    public int TimeoutMs { get; init; } = 90000;
    public double NoiseFloorThreshold { get; init; } = 400;
    // Opt-in audio audit ring: null/empty disables. Each extraction writes
    // mixture.wav + extracted.wav + meta.json; oldest pruned beyond AuditMaxPairs.
    public string? AuditDir { get; init; }
    public int AuditMaxPairs { get; init; } = 50;
}
```

`McpChannelVoice/Settings/VoiceSettings.cs` — add alongside the existing sub-settings properties (after `SpeakerVerification`):
```csharp
    public TseSettings Tse { get; init; } = new();
```

- [ ] **Step 2: Add the config skeletons (same commit — repo rule)**

`McpChannelVoice/appsettings.json` — add a top-level block (settings bind at root; follow the existing key style):
```json
    "Tse": {
        "Mode": "Off",
        "Endpoint": "http://tse-extractor:9098",
        "TimeoutMs": 90000,
        "NoiseFloorThreshold": 400,
        "AuditDir": "",
        "AuditMaxPairs": 50
    },
```

`DockerCompose/docker-compose.yml`, `mcp-channel-voice` service: add to `environment` (style precedent: `Announce__Escalation__WebhookUrl`):
```yaml
      Tse__Mode: "Off"
      Tse__Endpoint: "http://tse-extractor:9098"
      Tse__AuditDir: ""
```
and to its `volumes`:
```yaml
      - ./volumes/tse-audit:/tse-audit
```
(Only the keys an operator flips at deploy time go in compose; the rest ride appsettings defaults. `AuditDir` is set to `/tse-audit` when the operator opts in.)

- [ ] **Step 3: Verify build + config parse**

Run: `dotnet build agent.sln`
Expected: clean. Then `docker compose -f DockerCompose/docker-compose.yml config --quiet` — exit 0 (YAML valid).

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Settings/TseSettings.cs McpChannelVoice/Settings/VoiceSettings.cs McpChannelVoice/appsettings.json DockerCompose/docker-compose.yml
git commit -m "feat(voice): TseSettings with compose/appsettings skeletons"
```

---

### Task 3: `WavCodec`

**Files:**
- Create: `McpChannelVoice/Services/Tse/WavCodec.cs`
- Test: `Tests/Unit/McpChannelVoice/WavCodecTests.cs`

**Interfaces:**
- Consumes: `AudioChunk { ReadOnlyMemory<byte> Data; AudioFormat Format; TimeSpan Timestamp }`, `AudioFormat.WyomingStandard` (16 kHz / 2 bytes / mono).
- Produces: `WavCodec.Encode(IReadOnlyList<AudioChunk> chunks) -> byte[]` (RIFF WAV, 16 kHz mono S16LE) and `WavCodec.Decode(byte[] wav) -> AudioChunk` (single chunk, `AudioFormat.WyomingStandard`; throws `InvalidDataException` on non-RIFF/absent data chunk). Task 7 uses both.

- [ ] **Step 1: Write the failing test**

`Tests/Unit/McpChannelVoice/WavCodecTests.cs`:
```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tse;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class WavCodecTests
{
    private static AudioChunk Chunk(params byte[] data) =>
        new() { Data = data, Format = AudioFormat.WyomingStandard };

    [Fact]
    public void EncodeWritesCanonical16kMonoHeader()
    {
        var wav = WavCodec.Encode([Chunk(1, 2, 3, 4)]);
        wav.Length.ShouldBe(44 + 4);
        System.Text.Encoding.ASCII.GetString(wav, 0, 4).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).ShouldBe("WAVE");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // channels
        BitConverter.ToInt32(wav, 24).ShouldBe(16000);          // sample rate
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);      // bits/sample
        BitConverter.ToInt32(wav, 40).ShouldBe(4);              // data length
    }

    [Fact]
    public void RoundTripPreservesPayloadAcrossChunks()
    {
        var wav = WavCodec.Encode([Chunk(10, 11), Chunk(12, 13, 14)]);
        var decoded = WavCodec.Decode(wav);
        decoded.Data.ToArray().ShouldBe(new byte[] { 10, 11, 12, 13, 14 });
        decoded.Format.ShouldBe(AudioFormat.WyomingStandard);
    }

    [Fact]
    public void DecodeSkipsForeignSubChunksBeforeData()
    {
        var wav = WavCodec.Encode([Chunk(9, 9)]).ToList();
        // Splice a 4-byte "LIST" sub-chunk between "fmt " and "data" (offset 36).
        wav.InsertRange(36, "LIST"u8.ToArray().Concat(BitConverter.GetBytes(4)).Concat(new byte[] { 0, 0, 0, 0 }));
        var patched = wav.ToArray();
        BitConverter.GetBytes(patched.Length - 8).CopyTo(patched, 4); // fix RIFF size
        WavCodec.Decode(patched).Data.ToArray().ShouldBe(new byte[] { 9, 9 });
    }

    [Fact]
    public void DecodeRejectsNonRiff()
    {
        Should.Throw<InvalidDataException>(() => WavCodec.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WavCodecTests"`
Expected: compile FAIL (`WavCodec` does not exist).

- [ ] **Step 3: Implement**

`McpChannelVoice/Services/Tse/WavCodec.cs`:
```csharp
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services.Tse;

// Minimal RIFF codec for the hub's fixed interchange format (16 kHz mono S16LE). Encode is used
// to ship a capture to the tse-extractor sidecar; Decode wraps its reply for the inner STT.
public static class WavCodec
{
    public static byte[] Encode(IReadOnlyList<AudioChunk> chunks)
    {
        var format = AudioFormat.WyomingStandard;
        var dataLen = chunks.Sum(c => c.Data.Length);
        using var ms = new MemoryStream(44 + dataLen);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataLen);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);                                           // PCM
        w.Write((short)format.Channels);
        w.Write(format.SampleRateHz);
        w.Write(format.SampleRateHz * format.SampleWidthBytes * format.Channels);
        w.Write((short)(format.SampleWidthBytes * format.Channels)); // block align
        w.Write((short)(format.SampleWidthBytes * 8));               // bits/sample
        w.Write("data"u8);
        w.Write(dataLen);
        foreach (var chunk in chunks)
        {
            w.Write(chunk.Data.Span);
        }
        return ms.ToArray();
    }

    public static AudioChunk Decode(byte[] wav)
    {
        if (wav.Length < 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/WAVE payload");
        }
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.AsSpan(offset, 4);
            var size = BitConverter.ToInt32(wav, offset + 4);
            if (id.SequenceEqual("data"u8))
            {
                if (offset + 8 + size > wav.Length)
                {
                    throw new InvalidDataException("data sub-chunk overruns payload");
                }
                return new AudioChunk
                {
                    Data = wav.AsMemory(offset + 8, size),
                    Format = AudioFormat.WyomingStandard
                };
            }
            offset += 8 + size + (size & 1); // sub-chunks are word-aligned
        }
        throw new InvalidDataException("no data sub-chunk found");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WavCodecTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Tse/WavCodec.cs Tests/Unit/McpChannelVoice/WavCodecTests.cs
git commit -m "feat(voice): RIFF codec for the hub's 16k mono interchange format"
```

---

### Task 4: pinned `VoiceMetric` additions

**Files:**
- Modify: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`
- Modify: `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs`

**Interfaces:**
- Produces: `VoiceMetric.TseInvoked = 19, TseSkipped = 20, TseFailed = 21, TseLatencyMs = 22`. Task 7 publishes exactly these.

- [ ] **Step 1: Write the failing test**

Append to `VoiceEnumsTests` (same file, after the existing theories):
```csharp
    [Theory]
    [InlineData(VoiceMetric.TseInvoked, 19)]
    [InlineData(VoiceMetric.TseSkipped, 20)]
    [InlineData(VoiceMetric.TseFailed, 21)]
    [InlineData(VoiceMetric.TseLatencyMs, 22)]
    public void VoiceMetric_TseValues_ArePinned(VoiceMetric metric, int expected)
    {
        // Values persist as ints in Redis; a renumber silently re-labels historical data.
        ((int)metric).ShouldBe(expected);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: compile FAIL (members don't exist).

- [ ] **Step 3: Implement**

`Domain/DTOs/Metrics/Enums/VoiceMetric.cs` — append after `UtteranceRejected = 18` (keep the header comment untouched):
```csharp
    TseInvoked = 19,
    TseSkipped = 20,
    TseFailed = 21,
    TseLatencyMs = 22
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: PASS (all, incl. the 4 new pins).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/VoiceMetric.cs Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs
git commit -m "feat(metrics): pinned TSE voice metric values"
```

---

### Task 5: `ITseExtractorClient` / `TseExtractorClient`

**Files:**
- Create: `McpChannelVoice/Services/Tse/ITseExtractorClient.cs`
- Create: `McpChannelVoice/Services/Tse/TseExtractorClient.cs`
- Test: `Tests/Unit/McpChannelVoice/TseExtractorClientTests.cs`

**Interfaces:**
- Consumes: `TseSettings.Endpoint`/`TimeoutMs` (Task 2).
- Produces: `ITseExtractorClient.ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct) -> Task<byte[]?>` — extracted WAV bytes, or **null** on any unavailability (non-success status, timeout, transport error). Never throws except when the *caller's* token is cancelled. Task 7 consumes this contract.

- [ ] **Step 1: Write the failing test**

`Tests/Unit/McpChannelVoice/TseExtractorClientTests.cs`:
```csharp
using System.Net;
using McpChannelVoice.Services.Tse;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseExtractorClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return await respond(request);
        }
    }

    private static TseExtractorClient Client(StubHandler handler, int timeoutMs = 5000) =>
        new(new HttpClient(handler), new TseSettings { Endpoint = "http://tse-extractor:9098", TimeoutMs = timeoutMs },
            NullLogger<TseExtractorClient>.Instance);

    [Fact]
    public async Task SuccessReturnsBodyAndTargetsExtractRoute()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 8, 7])
        }));
        var result = await Client(handler).ExtractAsync([1, 2], "Dethon", CancellationToken.None);
        result.ShouldBe(new byte[] { 9, 8, 7 });
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://tse-extractor:9098/extract?speaker=Dethon");
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task UnknownSpeaker404ReturnsNull()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        (await Client(handler).ExtractAsync([1], "ghost", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task TransportErrorReturnsNull()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        (await Client(handler).ExtractAsync([1], "Dethon", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task DeadlineExpiryReturnsNull()
    {
        var handler = new StubHandler(async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        (await Client(handler, timeoutMs: 50).ExtractAsync([1], "Dethon", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var handler = new StubHandler(async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(50);
        await Should.ThrowAsync<OperationCanceledException>(
            () => Client(handler).ExtractAsync([1], "Dethon", cts.Token));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseExtractorClientTests"`
Expected: compile FAIL.

- [ ] **Step 3: Implement**

`McpChannelVoice/Services/Tse/ITseExtractorClient.cs`:
```csharp
namespace McpChannelVoice.Services.Tse;

public interface ITseExtractorClient
{
    // Extracted 16 kHz mono WAV bytes, or null when extraction is unavailable (service down,
    // deadline expired, unknown speaker, non-success status). Fail-open by contract: only the
    // caller's own cancellation surfaces as an exception.
    Task<byte[]?> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct);
}
```

`McpChannelVoice/Services/Tse/TseExtractorClient.cs`:
```csharp
using System.Net.Http.Headers;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tse;

public sealed class TseExtractorClient(
    HttpClient http, TseSettings settings, ILogger<TseExtractorClient> logger) : ITseExtractorClient
{
    public async Task<byte[]?> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(settings.TimeoutMs);
            using var content = new ByteArrayContent(mixtureWav);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            var url = $"{settings.Endpoint.TrimEnd('/')}/extract?speaker={Uri.EscapeDataString(speaker)}";
            using var response = await http.PostAsync(url, content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TSE extract for {Speaker} returned {Status}", speaker, response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "TSE extract for {Speaker} failed (fail-open, raw audio proceeds)", speaker);
            return null;
        }
    }
}
```
(The `when (!ct.IsCancellationRequested)` filter is the whole contract: deadline expiry and transport faults become null; the caller's cancellation rethrows.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseExtractorClientTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Tse/ITseExtractorClient.cs McpChannelVoice/Services/Tse/TseExtractorClient.cs Tests/Unit/McpChannelVoice/TseExtractorClientTests.cs
git commit -m "feat(voice): fail-open HTTP client for the tse-extractor sidecar"
```

---

### Task 6: `TseAuditTrail`

**Files:**
- Create: `McpChannelVoice/Services/Tse/TseAuditTrail.cs`
- Test: `Tests/Unit/McpChannelVoice/TseAuditTrailTests.cs`

**Interfaces:**
- Consumes: `TseSettings.AuditDir`/`AuditMaxPairs` values (passed as ctor args, not the record), `TimeProvider`.
- Produces: `TseAuditTrail(string? dir, int maxPairs, TimeProvider clock, ILogger<TseAuditTrail> logger)` with `void Record(string speaker, double? floorRms, long latencyMs, byte[] mixtureWav, byte[] extractedWav)`. Disabled (no-op) when `dir` is null/whitespace. Never throws. Task 7 calls `Record` after each successful extraction.

- [ ] **Step 1: Write the failing test**

`Tests/Unit/McpChannelVoice/TseAuditTrailTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using McpChannelVoice.Services.Tse;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseAuditTrailTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tse-audit-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero));

    private TseAuditTrail Trail(int maxPairs = 3) =>
        new(root, maxPairs, clock, NullLogger<TseAuditTrail>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void RecordWritesPairAndMetadata()
    {
        Trail().Record("Dethon", 512.5, 4200, [1, 2], [3, 4]);
        var dir = Directory.GetDirectories(root).ShouldHaveSingleItem();
        Path.GetFileName(dir).ShouldStartWith("20260722-100000");
        Path.GetFileName(dir).ShouldEndWith("-Dethon");
        File.ReadAllBytes(Path.Combine(dir, "mixture.wav")).ShouldBe(new byte[] { 1, 2 });
        File.ReadAllBytes(Path.Combine(dir, "extracted.wav")).ShouldBe(new byte[] { 3, 4 });
        var meta = File.ReadAllText(Path.Combine(dir, "meta.json"));
        meta.ShouldContain("\"speaker\":\"Dethon\"");
        meta.ShouldContain("\"latencyMs\":4200");
    }

    [Fact]
    public void PrunesOldestBeyondCap()
    {
        var trail = Trail(maxPairs: 3);
        for (var i = 0; i < 5; i++)
        {
            trail.Record("Dethon", null, i, [1], [2]);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var dirs = Directory.GetDirectories(root).Select(Path.GetFileName).Order().ToList();
        dirs.Count.ShouldBe(3);
        dirs[0]!.ShouldStartWith("20260722-100002"); // the two oldest were pruned
    }

    [Fact]
    public void NullDirIsDisabledNoOp()
    {
        var trail = new TseAuditTrail(null, 3, clock, NullLogger<TseAuditTrail>.Instance);
        trail.Record("Dethon", null, 1, [1], [2]);
        Directory.Exists(root).ShouldBeFalse();
    }
}
```
(`FakeTimeProvider` comes from `Microsoft.Extensions.TimeProvider.Testing` — grep `Tests/Tests.csproj` for it; if absent, check how existing tests fake `TimeProvider` and use that pattern instead.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseAuditTrailTests"`
Expected: compile FAIL.

- [ ] **Step 3: Implement**

`McpChannelVoice/Services/Tse/TseAuditTrail.cs`:
```csharp
using System.Text.Json;

namespace McpChannelVoice.Services.Tse;

// Opt-in audio audit ring for the TSE live trial: one directory per extraction
// (mixture + extracted + metadata), oldest pruned beyond the cap. Best-effort by
// design — an audit failure must never affect the turn, so everything is caught.
public sealed class TseAuditTrail(string? dir, int maxPairs, TimeProvider clock, ILogger<TseAuditTrail> logger)
{
    private bool Enabled => !string.IsNullOrWhiteSpace(dir);

    public void Record(string speaker, double? floorRms, long latencyMs, byte[] mixtureWav, byte[] extractedWav)
    {
        if (!Enabled)
        {
            return;
        }
        try
        {
            var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss-fff");
            var pairDir = Path.Combine(dir!, $"{stamp}-{speaker}");
            Directory.CreateDirectory(pairDir);
            File.WriteAllBytes(Path.Combine(pairDir, "mixture.wav"), mixtureWav);
            File.WriteAllBytes(Path.Combine(pairDir, "extracted.wav"), extractedWav);
            File.WriteAllText(Path.Combine(pairDir, "meta.json"), JsonSerializer.Serialize(new
            {
                speaker,
                floorRms,
                latencyMs,
                recordedAt = clock.GetUtcNow()
            }));
            Prune();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TSE audit write failed (turn unaffected)");
        }
    }

    private void Prune()
    {
        var dirs = Directory.GetDirectories(dir!).Order().ToList();
        foreach (var stale in dirs.Take(Math.Max(0, dirs.Count - maxPairs)))
        {
            Directory.Delete(stale, recursive: true);
        }
    }
}
```
(Lexicographic `Order()` on `yyyyMMdd-HHmmss-fff` prefixes is chronological — same trick the takes use.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseAuditTrailTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Tse/TseAuditTrail.cs Tests/Unit/McpChannelVoice/TseAuditTrailTests.cs
git commit -m "feat(voice): capped opt-in TSE audio audit ring"
```

---

### Task 7: `TseSpeechToText` decorator

**Files:**
- Create: `McpChannelVoice/Services/Tse/TseSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/TseSpeechToTextTests.cs`

**Interfaces:**
- Consumes: `ISpeechToText` (Domain contract), `TseSettings` (Task 2), `WavCodec` (Task 3), `VoiceMetric.Tse*` (Task 4), `ITseExtractorClient` (Task 5), `TseAuditTrail` (Task 6), `IMetricsPublisher` (Domain contract — grep `Domain/Contracts/IMetricsPublisher.cs` for the exact `PublishAsync` signature; the host calls `metrics.PublishAsync(new VoiceEvent { … }, ct)`).
- Produces: `TseSpeechToText : ISpeechToText` with `static ISpeechToText Wrap(ISpeechToText inner, TseSettings settings, ITseExtractorClient client, TseAuditTrail audit, IMetricsPublisher metrics, ILoggerFactory loggers)` returning `inner` unchanged when `settings.Mode == TseMode.Off`. Task 8 wires `Wrap` into DI.

**Behavior contract (the test matrix):**

| Mode | TargetSpeaker | FloorRms vs threshold | Client result | Inner receives | Metrics |
|------|--------------|----------------------|---------------|----------------|---------|
| Off (via Wrap) | – | – | – | (decorator absent) | none |
| Auto | null | – | not called | raw | TseSkipped/`no_speaker` |
| Auto | set | below | not called | raw | TseSkipped/`quiet` |
| Auto | set | at/above | null | raw | TseFailed |
| Auto | set | at/above | wav bytes | extracted | TseInvoked + TseLatencyMs |
| Always | set | below | wav bytes | extracted | TseInvoked + TseLatencyMs |

- [ ] **Step 1: Write the failing test**

`Tests/Unit/McpChannelVoice/TseSpeechToTextTests.cs`:
```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tse;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseSpeechToTextTests
{
    private sealed class RecordingInner : ISpeechToText
    {
        public byte[]? ReceivedPayload;
        public TranscriptionOptions? ReceivedOptions;
        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            var buffer = new List<byte>();
            await foreach (var chunk in audio.WithCancellation(ct))
            {
                buffer.AddRange(chunk.Data.ToArray());
            }
            ReceivedPayload = buffer.ToArray();
            ReceivedOptions = options;
            return new TranscriptionResult { Text = "ok" };
        }
    }

    private sealed class StubClient(byte[]? reply) : ITseExtractorClient
    {
        public (byte[] Wav, string Speaker)? LastCall;
        public Task<byte[]?> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct)
        {
            LastCall = (mixtureWav, speaker);
            return Task.FromResult(reply);
        }
    }

    private sealed class RecordingMetrics : IMetricsPublisher
    {
        public readonly List<VoiceEvent> Events = [];
        public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            if (metricEvent is VoiceEvent voice) Events.Add(voice);
            return Task.CompletedTask;
        }
    }

    private static readonly byte[] RawPcm = [1, 2, 3, 4, 5, 6];

    private static async IAsyncEnumerable<AudioChunk> Chunks()
    {
        yield return new AudioChunk { Data = RawPcm, Format = AudioFormat.WyomingStandard };
        await Task.CompletedTask;
    }

    private static TranscriptionOptions Options(string? speaker = "Dethon", double? floor = 900) =>
        new() { TargetSpeaker = speaker, NoiseFloorRms = floor, Language = "es" };

    private static (ISpeechToText Stt, RecordingInner Inner, StubClient Client, RecordingMetrics Metrics) Build(
        TseMode mode, byte[]? clientReply)
    {
        var inner = new RecordingInner();
        var client = new StubClient(clientReply);
        var metrics = new RecordingMetrics();
        var audit = new TseAuditTrail(null, 1, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance);
        var settings = new TseSettings { Mode = mode, NoiseFloorThreshold = 400 };
        var stt = TseSpeechToText.Wrap(inner, settings, client, audit, metrics, NullLoggerFactory.Instance);
        return (stt, inner, client, metrics);
    }

    [Fact]
    public void OffModeWrapsNothing()
    {
        var inner = new RecordingInner();
        TseSpeechToText.Wrap(inner, new TseSettings { Mode = TseMode.Off }, new StubClient(null),
                new TseAuditTrail(null, 1, new FakeTimeProvider(), NullLogger<TseAuditTrail>.Instance),
                new RecordingMetrics(), NullLoggerFactory.Instance)
            .ShouldBeSameAs(inner);
    }

    [Fact]
    public async Task AutoWithoutSpeakerSkipsToRaw()
    {
        var (stt, inner, client, metrics) = Build(TseMode.Auto, clientReply: null);
        await stt.TranscribeAsync(Chunks(), Options(speaker: null), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(RawPcm);
        client.LastCall.ShouldBeNull();
        metrics.Events.ShouldHaveSingleItem().Metric.ShouldBe(VoiceMetric.TseSkipped);
        metrics.Events[0].Outcome.ShouldBe("no_speaker");
    }

    [Fact]
    public async Task AutoQuietFloorSkipsToRaw()
    {
        var (stt, inner, client, metrics) = Build(TseMode.Auto, clientReply: null);
        await stt.TranscribeAsync(Chunks(), Options(floor: 100), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(RawPcm);
        client.LastCall.ShouldBeNull();
        metrics.Events.ShouldHaveSingleItem().Outcome.ShouldBe("quiet");
    }

    [Fact]
    public async Task AutoNoisyFailureFallsBackToRaw()
    {
        var (stt, inner, client, metrics) = Build(TseMode.Auto, clientReply: null);
        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(RawPcm);
        client.LastCall.ShouldNotBeNull();
        metrics.Events.ShouldHaveSingleItem().Metric.ShouldBe(VoiceMetric.TseFailed);
    }

    [Fact]
    public async Task AutoNoisySuccessFeedsExtractedAudioToInner()
    {
        var extractedPcm = new byte[] { 40, 41, 42, 43 };
        var reply = WavCodec.Encode([new AudioChunk { Data = extractedPcm, Format = AudioFormat.WyomingStandard }]);
        var (stt, inner, client, metrics) = Build(TseMode.Auto, reply);
        var result = await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        result.Text.ShouldBe("ok");
        inner.ReceivedPayload.ShouldBe(extractedPcm);
        inner.ReceivedOptions!.Language.ShouldBe("es");
        client.LastCall!.Value.Speaker.ShouldBe("Dethon");
        WavCodec.Decode(client.LastCall.Value.Wav).Data.ToArray().ShouldBe(RawPcm);
        metrics.Events.Select(e => e.Metric).ShouldBe([VoiceMetric.TseInvoked, VoiceMetric.TseLatencyMs]);
        metrics.Events[0].Identity.ShouldBe("Dethon");
    }

    [Fact]
    public async Task AlwaysModeIgnoresFloor()
    {
        var reply = WavCodec.Encode([new AudioChunk { Data = new byte[] { 7 }, Format = AudioFormat.WyomingStandard }]);
        var (stt, inner, client, _) = Build(TseMode.Always, reply);
        await stt.TranscribeAsync(Chunks(), Options(floor: 0), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(new byte[] { 7 });
        client.LastCall.ShouldNotBeNull();
    }

    [Fact]
    public async Task GarbageReplyFallsBackToRaw()
    {
        var (stt, inner, _, metrics) = Build(TseMode.Auto, clientReply: [1, 2, 3]); // not RIFF
        await stt.TranscribeAsync(Chunks(), Options(), CancellationToken.None);
        inner.ReceivedPayload.ShouldBe(RawPcm);
        metrics.Events.ShouldHaveSingleItem().Metric.ShouldBe(VoiceMetric.TseFailed);
    }
}
```
Note: verify `IMetricsPublisher.PublishAsync`'s exact signature before writing `RecordingMetrics` — adjust the stub to match (the host's usage is `metrics.PublishAsync(new VoiceEvent { … }, ct)`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseSpeechToTextTests"`
Expected: compile FAIL.

- [ ] **Step 3: Implement**

`McpChannelVoice/Services/Tse/TseSpeechToText.cs`:
```csharp
using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tse;

// STT-path target-speaker extraction (spec: 2026-07-22-tse-live-integration-design.md).
// Sits between the host and the segmented/wyoming STT chain. Fail-open on every path: the
// inner backend always gets audio — extracted when the sidecar delivered, raw otherwise.
// The gate and endpointing never see this class; only the STT input changes.
public sealed class TseSpeechToText(
    ISpeechToText inner,
    TseSettings settings,
    ITseExtractorClient client,
    TseAuditTrail audit,
    IMetricsPublisher metrics,
    ILogger<TseSpeechToText> logger) : ISpeechToText
{
    public static ISpeechToText Wrap(
        ISpeechToText inner, TseSettings settings, ITseExtractorClient client, TseAuditTrail audit,
        IMetricsPublisher metrics, ILoggerFactory loggers) =>
        settings.Mode == TseMode.Off
            ? inner
            : new TseSpeechToText(inner, settings, client, audit, metrics, loggers.CreateLogger<TseSpeechToText>());

    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
    {
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            chunks.Add(chunk);
        }

        var skip = SkipReason(options);
        if (skip is not null)
        {
            await PublishAsync(VoiceMetric.TseSkipped, options, outcome: skip, ct: ct);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        var mixture = WavCodec.Encode(chunks);
        var stopwatch = Stopwatch.StartNew();
        var reply = await client.ExtractAsync(mixture, options.TargetSpeaker!, ct);
        stopwatch.Stop();

        AudioChunk extracted;
        try
        {
            if (reply is null)
            {
                throw new InvalidDataException("sidecar unavailable");
            }
            extracted = WavCodec.Decode(reply);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "TSE extraction unavailable for {Speaker}; raw audio proceeds", options.TargetSpeaker);
            await PublishAsync(VoiceMetric.TseFailed, options, durationMs: stopwatch.ElapsedMilliseconds, ct: ct);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        await PublishAsync(VoiceMetric.TseInvoked, options, outcome: "ok", ct: ct);
        await PublishAsync(VoiceMetric.TseLatencyMs, options, durationMs: stopwatch.ElapsedMilliseconds, ct: ct);
        audit.Record(options.TargetSpeaker!, options.NoiseFloorRms, stopwatch.ElapsedMilliseconds, mixture, reply!);
        return await inner.TranscribeAsync(Replay([extracted]), options, ct);
    }

    private string? SkipReason(TranscriptionOptions options) =>
        options.TargetSpeaker is null ? "no_speaker"
        : settings.Mode == TseMode.Auto && (options.NoiseFloorRms ?? 0) < settings.NoiseFloorThreshold ? "quiet"
        : null;

    private Task PublishAsync(
        VoiceMetric metric, TranscriptionOptions options, string? outcome = null, long? durationMs = null,
        CancellationToken ct = default) =>
        metrics.PublishAsync(new VoiceEvent
        {
            Metric = metric,
            Identity = options.TargetSpeaker,
            Outcome = outcome,
            DurationMs = durationMs,
            FloorRms = options.NoiseFloorRms
        }, ct);

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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseSpeechToTextTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Tse/TseSpeechToText.cs Tests/Unit/McpChannelVoice/TseSpeechToTextTests.cs
git commit -m "feat(voice): fail-open TSE decorator on the STT path"
```

---

### Task 8: DI wiring + full suite

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (the `AddSingleton<ISpeechToText>` factory at ~line 75, plus two new registrations)

**Interfaces:**
- Consumes: everything from Tasks 2-7.
- Produces: the composed chain `TseSpeechToText.Wrap(SegmentedSpeechToText.Wrap(WyomingSpeechToText))` behind `ISpeechToText`.

- [ ] **Step 1: Wire the registrations**

In `ConfigModule.cs`, above the `ISpeechToText` registration add:
```csharp
        services.AddSingleton<Services.Tse.ITseExtractorClient>(sp =>
            new Services.Tse.TseExtractorClient(
                new HttpClient(),
                settings.Tse,
                sp.GetRequiredService<ILogger<Services.Tse.TseExtractorClient>>()));
        services.AddSingleton(sp => new Services.Tse.TseAuditTrail(
            settings.Tse.AuditDir,
            settings.Tse.AuditMaxPairs,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Services.Tse.TseAuditTrail>>()));
```
and change the `ISpeechToText` factory's return to:
```csharp
            var segmented = McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, settings.WyomingClient, sp.GetRequiredService<ILoggerFactory>());
            return Services.Tse.TseSpeechToText.Wrap(
                segmented,
                settings.Tse,
                sp.GetRequiredService<Services.Tse.ITseExtractorClient>(),
                sp.GetRequiredService<Services.Tse.TseAuditTrail>(),
                sp.GetRequiredService<Domain.Contracts.IMetricsPublisher>(),
                sp.GetRequiredService<ILoggerFactory>());
```
(Verify `TimeProvider` and `IMetricsPublisher` are registered in this container — the host and `VoiceConversationManager` already resolve them, so they are; if the exact service key differs, mirror how `WyomingSatelliteHost`'s ctor gets `metrics`.)

- [ ] **Step 2: Build + full unit suite**

Run: `dotnet build agent.sln` → clean.
Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"` → all green (judge failures by type per the project baseline — no new failures attributable to this change).

- [ ] **Step 3: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs
git commit -m "feat(voice): wire TSE decorator, client and audit ring into DI"
```

---

### Task 9: `tse-extractor` sidecar container

**Files:**
- Create: `DockerCompose/tse-extractor/Dockerfile`
- Create: `DockerCompose/tse-extractor/app.py`
- Create: `DockerCompose/tse-extractor/entrypoint.sh`
- Modify: `DockerCompose/docker-compose.yml` (new service)

**Interfaces:**
- Consumes: the pinned checkpoint/commit identities from Global Constraints; the `./volumes/voices` layout (`<speaker>/enroll-*.wav`, 16 kHz mono S16LE).
- Produces: HTTP on `:9098` — `GET /health` → `{"status":"ready","speakers":[...]}`; `POST /extract?speaker=<name>` with WAV body → 200 WAV | 404 unknown speaker | 422 no speech. Task 10's integration test and the hub's `TseExtractorClient` consume this.

- [ ] **Step 1: Write the Dockerfile**

`DockerCompose/tse-extractor/Dockerfile`:
```dockerfile
FROM python:3.11-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl libsndfile1 \
    && rm -rf /var/lib/apt/lists/*

# torch first (CPU wheels; index serves x86_64 and aarch64), then the wesep/wespeaker
# import tree. numpy<2 for wespeaker@e9bbf73 compatibility. wespeaker --no-deps so it
# cannot drag a different torch or the heavy s3prl frontend HEAD added.
RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu torch torchaudio \
    && pip install --no-cache-dir "numpy<2" soundfile pyyaml scipy silero-vad tqdm \
        kaldiio hdbscan umap-learn scikit-learn onnxruntime requests flask \
    && pip install --no-cache-dir --no-deps \
        "wespeaker @ git+https://github.com/wenet-e2e/wespeaker.git@e9bbf73d0fd13db6cf42a6cb2eafb0d7dd0f8e0e"

RUN git clone https://github.com/wenet-e2e/wesep /opt/wesep-src \
    && git -C /opt/wesep-src checkout 99eca54b60300d39b9353d93cf285a14bba37854

COPY app.py entrypoint.sh /opt/tse/
RUN chmod +x /opt/tse/entrypoint.sh

ENV WESEP_SRC=/opt/wesep-src \
    MODEL_DIR=/models/wesep-english \
    VOICES_DIR=/voices

EXPOSE 9098
ENTRYPOINT ["/opt/tse/entrypoint.sh"]
```
(If `hdbscan` has no aarch64 wheel at build time, add `build-essential` to the apt line — known fallback, note it in the report if used.)

- [ ] **Step 2: Write the entrypoint (checkpoint provisioning)**

`DockerCompose/tse-extractor/entrypoint.sh`:
```sh
#!/bin/sh
set -e
CKPT_DIR="${MODEL_DIR:-/models/wesep-english}"
if [ ! -f "$CKPT_DIR/avg_model.pt" ] || [ ! -f "$CKPT_DIR/config.yaml" ]; then
    echo "provisioning bsrnn_ecapa_vox1 checkpoint into $CKPT_DIR"
    mkdir -p "$CKPT_DIR"
    curl -fSL --retry 3 -o "$CKPT_DIR/ckpt.tar.gz" \
        "https://www.modelscope.cn/datasets/wenet/wesep_pretrained_models/resolve/master/bsrnn_ecapa_vox1.tar.gz"
    tar xzf "$CKPT_DIR/ckpt.tar.gz" -C "$CKPT_DIR"
    rm "$CKPT_DIR/ckpt.tar.gz"
fi
exec python /opt/tse/app.py
```

- [ ] **Step 3: Write the app**

`DockerCompose/tse-extractor/app.py`:
```python
"""Target-speaker extraction sidecar (spec: 2026-07-22-tse-live-integration-design.md).

Loads the WeSep BSRNN+ECAPA checkpoint once at startup and serves one extraction per
request under a lock (utterances arrive seconds apart; the hub enforces the deadline).
Enrollment is the hub's voices volume: /voices/<speaker>/*.wav, concatenated and cached,
invalidated whenever the directory's (name, size, mtime) signature changes.
"""
import json
import os
import sys
import tempfile
import threading
from pathlib import Path

import numpy as np
import soundfile as sf
from flask import Flask, Response, request

sys.path.insert(0, os.environ.get("WESEP_SRC", "/opt/wesep-src"))
from wesep.cli.extractor import load_model_local  # noqa: E402

VOICES = Path(os.environ.get("VOICES_DIR", "/voices"))
MODEL_DIR = os.environ.get("MODEL_DIR", "/models/wesep-english")
CACHE = Path(os.environ.get("ENROLL_CACHE", "/tmp/enroll-cache"))

app = Flask(__name__)
lock = threading.Lock()
extractor = load_model_local(MODEL_DIR)
extractor.set_device("cpu")


def _speakers():
    if not VOICES.is_dir():
        return []
    return sorted(p.name for p in VOICES.iterdir() if p.is_dir() and any(p.glob("*.wav")))


def _signature(speaker_dir):
    return json.dumps(sorted(
        (f.name, f.stat().st_size, f.stat().st_mtime) for f in speaker_dir.glob("*.wav")))


def _enrollment_wav(speaker):
    """Concatenated enrollment for the speaker (all takes), cached; None if unknown."""
    speaker_dir = VOICES / speaker
    takes = sorted(speaker_dir.glob("*.wav")) if speaker_dir.is_dir() else []
    if not takes:
        return None
    CACHE.mkdir(parents=True, exist_ok=True)
    target = CACHE / f"{speaker}.wav"
    sig_file = CACHE / f"{speaker}.sig"
    sig = _signature(speaker_dir)
    if not (target.exists() and sig_file.exists() and sig_file.read_text() == sig):
        parts = [sf.read(str(t), dtype="float32")[0] for t in takes]
        sf.write(str(target), np.concatenate(parts), 16000, subtype="PCM_16")
        sig_file.write_text(sig)
    return target


@app.get("/health")
def health():
    return {"status": "ready", "speakers": _speakers()}


@app.post("/extract")
def extract():
    speaker = request.args.get("speaker", "")
    enrollment = _enrollment_wav(speaker)
    if enrollment is None:
        return Response(f"unknown speaker {speaker!r}", status=404)
    with tempfile.TemporaryDirectory(prefix="tse-") as td:
        mix = Path(td) / "mix.wav"
        out = Path(td) / "out.wav"
        mix.write_bytes(request.get_data())
        with lock:
            speech = extractor.extract_speech(str(mix), str(enrollment))
        if speech is None:
            return Response("extraction returned no speech", status=422)
        sf.write(str(out), speech[0].detach().cpu().numpy(), 16000, subtype="PCM_16")
        return Response(out.read_bytes(), mimetype="audio/wav")


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=9098, threaded=True)
```

- [ ] **Step 4: Add the compose service**

`DockerCompose/docker-compose.yml` — new service (mirror the logging/network style of the neighbors; port published for the integration test):
```yaml
  tse-extractor:
    image: tse-extractor:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: tse-extractor
    build:
      context: ./tse-extractor
    restart: unless-stopped
    ports:
      - "9098:9098"
    volumes:
      - ./volumes/tse-models:/models
      - ./volumes/voices:/voices:ro
    networks:
      - jackbot
```

- [ ] **Step 5: Build and smoke locally**

```bash
docker build -t tse-extractor:latest DockerCompose/tse-extractor       # plain docker build (compose cache-importer gotcha)
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d tse-extractor
```
First start downloads the ~262 MB checkpoint into `volumes/tse-models` (watch `docker logs -f tse-extractor` until Flask binds). Then:
```bash
curl -s http://localhost:9098/health
```
Expected: `{"speakers": [...], "status": "ready"}` — speakers listed if `DockerCompose/volumes/voices` has enrollment dirs on this machine, else `[]`.
```bash
curl -s -o /dev/null -w "%{http_code}\n" -X POST "http://localhost:9098/extract?speaker=nobody" --data-binary @/dev/null
```
Expected: `404`. If a real speaker exists locally, run one real extract with any 16 kHz mono WAV and confirm 200 + WAV bytes back (`file` says RIFF). Record actual timings in the report.

- [ ] **Step 6: Commit**

```bash
git add DockerCompose/tse-extractor/ DockerCompose/docker-compose.yml
git commit -m "feat(tse): tse-extractor sidecar container (wesep bsrnn_ecapa_vox1)"
```

---

### Task 10: integration test + trial runbook

**Files:**
- Create: `Tests/Integration/McpChannelVoice/TseExtractorServiceTests.cs`
- Create: `docs/tse-trial-runbook.md`

**Interfaces:**
- Consumes: the sidecar's HTTP contract (Task 9), `WavCodec` (Task 3).

- [ ] **Step 1: Write the integration test**

`Tests/Integration/McpChannelVoice/TseExtractorServiceTests.cs` (mirror the trait/skip conventions of the neighboring integration tests — grep `Tests/Integration/McpChannelVoice/*.cs` for the `[Trait]` pattern used):
```csharp
using System.Net;
using System.Text.Json;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tse;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

[Trait("Category", "Integration")]
public class TseExtractorServiceTests
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://localhost:9098") };

    private static byte[] SyntheticUtteranceWav(double seconds = 2.0)
    {
        var samples = (int)(16000 * seconds);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / 16000.0));
            BitConverter.GetBytes(value).CopyTo(pcm, i * 2);
        }
        return WavCodec.Encode([new AudioChunk { Data = pcm, Format = AudioFormat.WyomingStandard }]);
    }

    [Fact]
    public async Task HealthReportsReadyAndSpeakers()
    {
        var body = await Http.GetStringAsync("/health");
        var doc = JsonDocument.Parse(body).RootElement;
        doc.GetProperty("status").GetString().ShouldBe("ready");
        doc.GetProperty("speakers").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task UnknownSpeakerIs404()
    {
        using var content = new ByteArrayContent(SyntheticUtteranceWav());
        var response = await Http.PostAsync("/extract?speaker=nobody-here", content);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ExtractRoundTripsForAnEnrolledSpeaker()
    {
        var health = JsonDocument.Parse(await Http.GetStringAsync("/health")).RootElement;
        var speakers = health.GetProperty("speakers").EnumerateArray().Select(s => s.GetString()).ToList();
        if (speakers.Count == 0)
        {
            return; // no enrollment on this machine; the 404 test still covers the routing
        }
        var wav = SyntheticUtteranceWav();
        using var content = new ByteArrayContent(wav);
        var response = await Http.PostAsync($"/extract?speaker={speakers[0]}", content);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reply = await response.Content.ReadAsByteArrayAsync();
        var decoded = WavCodec.Decode(reply); // valid RIFF, hub format
        decoded.Data.Length.ShouldBeGreaterThan(0);
    }
}
```

- [ ] **Step 2: Run it (sidecar up from Task 9)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TseExtractorServiceTests"`
Expected: PASS (3 tests; the round-trip one may early-return without local enrollment).

- [ ] **Step 3: Write the runbook**

`docs/tse-trial-runbook.md`:
```markdown
# TSE Live-Trial Runbook

Spec: `superpowers/specs/2026-07-22-tse-live-integration-design.md`. Everything here is
config-only; no code changes during the trial.

## Enable on a deployment (pi5 today, AI 370 later)

1. Deploy/update the stack so `tse-extractor` is running (`docker logs tse-extractor`
   shows the checkpoint provisioned and Flask bound; `curl :9098/health` lists speakers).
2. On `mcp-channel-voice`, set (compose env on the deployment host):
   - `Tse__Mode: "Auto"` (or `"Always"` for a diagnostic session)
   - `Tse__AuditDir: "/tse-audit"` to opt into the audio audit ring
   - pi5 only: consider `Tse__TimeoutMs: "90000"` (default) — extraction on Pi 5 CPU is
     expected to take tens of seconds; Auto keeps quiet turns fast.
3. Restart `mcp-channel-voice`. Mode changes always need a restart.

## Calibrate `Tse__NoiseFloorThreshold`

`FloorRms` is published on existing voice metrics (UtteranceRejected/SttLatencyMs events)
and on every Tse* event. Pull recent values from the dashboard (voice metrics) or Redis and
pick a threshold between the quiet-room band and the TV-on band. Default 400 is provisional.
Too many `TseSkipped/quiet` on TV turns → lower it; extractions firing in silence → raise it.

## Watch during the trial

- Dashboard voice metrics: `TseInvoked` / `TseSkipped` (Outcome quiet|no_speaker) /
  `TseFailed` counts, `TseLatencyMs` distribution (this IS the deployability number).
- Audit pairs under `DockerCompose/volumes/tse-audit/` — listen to mixture vs extracted for
  a few TV-heavy turns (`scp` them off the pi; newest 50 kept).
- `docker logs mcp-channel-voice` — decorator logs every fail-open fallback with a reason.
- Gate behavior in noise (observational, feeds the v2 reverify question): rejected-utterance
  metrics vs floor level.

## Readout (spec §Trial Readout)

1. `TseLatencyMs` on pi5, later AI 370.
2. Invoked-turn transcript quality (audit pairs + daily use).
3. Quiet-path check: skip rate ≈ 100 % in quiet scenes, latency unchanged there.
4. Gate accept/identify vs floor (observational).

Kill switch: `Tse__Mode: "Off"` + restart. Worst case during the trial: slower noisy turns;
quiet turns and all failures fall back to today's raw path.
```

- [ ] **Step 4: Full suite + format check**

Run: `dotnet build agent.sln && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`
Expected: clean build, unit suite green (integration suite needs docker services — run the Tse one only, as above).

- [ ] **Step 5: Commit**

```bash
git add Tests/Integration/McpChannelVoice/TseExtractorServiceTests.cs docs/tse-trial-runbook.md
git commit -m "feat(tse): sidecar integration test and live-trial runbook"
```

---

## Self-Review Notes

- Spec coverage: sidecar (T9), decorator + policy + fail-open (T7), options plumbing (T1), settings/infra same-change rule (T2), audit ring (T6), pinned metrics (T4), DI (T8), integration test + runbook/trial guidance (T10). WavCodec (T3) and the client (T5) are the supporting seams. The spec's "AI 370 fallback: run sidecar on WSL" needs no task — it's `Tse__Endpoint` config.
- Type consistency: `TseSettings` names (T2) match usages in T5/T7/T8; `VoiceMetric.Tse*` (T4) match T7's publishes; `WavCodec.Encode/Decode` signatures (T3) match T7/T10; `ExtractAsync(byte[], string, CancellationToken)` (T5) matches T7's stub.
- Deliberate judgment points for implementers: `SatelliteConfig` construction in T1's test (copy the existing test pattern), `IMetricsPublisher.PublishAsync` exact signature (T7 note), integration-test trait conventions (T10 note), `FakeTimeProvider` package availability (T6 note).
