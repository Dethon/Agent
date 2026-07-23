# Voice STT/TTS Migration to Lemonade — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two Wyoming voice-engine containers (`wyoming-whisper`, `wyoming-piper`) with one Lemonade Server container serving OpenAI-compatible STT (`/v1/audio/transcriptions`, whisper.cpp, Vulkan iGPU) and streaming TTS (`/v1/audio/speech`, Kokoro), as a hard cutover.

**Architecture:** The hub↔satellite Wyoming transport is untouched. Only the two engine backends behind `ISpeechToText`/`ITextToSpeech` are swapped: `OpenAiSpeechToText` buffers a segment to WAV and POSTs it with `response_format=verbose_json` (preserving the avg_logprob/no_speech_prob gibberish gate), `OpenAiTextToSpeech` streams 24 kHz PCM incrementally and resamples it to the satellites' fixed 22 050 Hz sink via a stateful rational (147/160) resampler. A custom Lemonade image + entrypoint maps one `STT_BACKEND` env var (`cpu`|`gpu`) to the whisper.cpp device.

**Tech Stack:** .NET 10 (`LangVersion 14`), `HttpClient`/`IHttpClientFactory`, System.Text.Json `JsonNode`, xUnit + Shouldly + Moq, Docker Compose, Lemonade Server (`ghcr.io/lemonade-sdk/lemonade-server:v11.0.0`).

**Spec:** `docs/superpowers/specs/2026-07-18-voice-lemonade-migration-design.md`

## Global Constraints

- Hard cutover: **no Wyoming STT/TTS fallback path, no dual config**. Wyoming satellite-transport code (`WyomingSatelliteHost`, `WyomingClient`, `Services/WyomingProtocol/*`, `SilenceGate`, `WyomingClientSettings`) is **kept untouched**, as are the `SegmentedSpeechToText` and `SilenceTrimmingTextToSpeech` wrappers.
- All hub-emitted audio must be **22 050 Hz mono s16le** (the satellite playback sink is fixed and ignores announced rates).
- Names/values (use verbatim): image `ghcr.io/lemonade-sdk/lemonade-server:v11.0.0`; compose service `mcp-lemonade`, port `13305`; env var `STT_BACKEND` ∈ {`cpu`,`gpu`}, default `gpu` (Lemonade-container-side only — see below); STT model `Whisper-Medium`; TTS model `kokoro-v1`, default voice `ef_dora`; hub base URL `http://mcp-lemonade:13305/v1`; resample ratio 24000→22050 = **147/160**. Optional NPU tier: `docker-compose.override.npu.yml` + `STT_MODEL` (Lemonade `flm` recipe, same service and port).
- **The NPU tier runs inside Lemonade via its `flm` recipe** (the integrated path at <https://lemonade-server.ai/flm_npu_linux.html>): Lemonade auto-installs and supervises the FastFlowLM binary as a backend subprocess. It is therefore the **same container, same base URL, different model name** — enabled by an opt-in `docker-compose.override.npu.yml` (device + memlock + `STT_MODEL`), never a second service. Lemonade's *whisper.cpp* NPU backend is separately Windows-only, which is why `STT_BACKEND` stays {`cpu`,`gpu`}: it selects the whisper.cpp device, a different knob from the `flm` recipe. Within Lemonade, `cpu` and `gpu` run the same engine and same model — only the device flips — so `STT_BACKEND` stays purely a container-side concern.
- Gate thresholds: drop when `AvgLogProb < -1.0` (floor) or `NoSpeechProb > 0.6` (ceiling); **null signals always fail open**.
- Env-var rule (CLAUDE.md): compose + appsettings skeleton land in the same change as the code that reads them. No `DockerCompose/.env` entries — Lemonade is local and unauthenticated (no secrets).
- TDD: write the failing test first and **capture the RED failure output** before implementing (project rule).
- `.cs` files have **no trailing newline**; file-scoped namespaces; primary constructors; prefer LINQ (loops allowed in the hot audio path); no XML doc comments; Shouldly assertions.
- Commit after each task **on the currently checked-out branch** (never switch branches uninvited). The pre-commit hook re-stages whole files — make the working tree match the commit you want.
- Test naming: `{Method}_{Scenario}_{ExpectedResult}`; unit tests under `Tests/Unit/McpChannelVoice/`.

## File Map (whole migration)

| Action | Path |
|---|---|
| Create | `McpChannelVoice/Services/Tts/PcmStreamResampler.cs` |
| Create | `McpChannelVoice/Services/Tts/OpenAiTextToSpeech.cs` |
| Create | `McpChannelVoice/Services/Stt/OpenAiSpeechToText.cs` |
| Create | `Tests/Unit/McpChannelVoice/Tts/PcmStreamResamplerTests.cs` |
| Create | `Tests/Unit/McpChannelVoice/Tts/OpenAiTextToSpeechTests.cs` |
| Create | `Tests/Unit/McpChannelVoice/Stt/OpenAiSpeechToTextTests.cs` |
| Create | `Tests/Unit/McpChannelVoice/ConfigModuleTests.cs` |
| Create | `DockerCompose/lemonade/Dockerfile`, `DockerCompose/lemonade/entrypoint.sh`, `DockerCompose/lemonade/smoke.sh` |
| Create | `DockerCompose/docker-compose.override.npu.yml` (optional NPU tier, Task 8) |
| Modify | `McpChannelVoice/Settings/{SttSettings,TtsSettings,VoiceSettings}.cs` |
| Modify | `McpChannelVoice/Services/TranscriptDispatcher.cs`, `Domain/DTOs/Voice/TranscriptionResult.cs` (comment only) |
| Modify | `McpChannelVoice/Modules/ConfigModule.cs` |
| Modify | `McpChannelVoice/Services/{WyomingSatelliteHost,AnnouncementService,InsistentAnnouncementController}.cs`, `McpChannelVoice/McpTools/{SendReplyTool,RequestApprovalTool}.cs` (voice/language resolution sites) |
| Modify | `McpChannelVoice/appsettings.json`, `Tests/Unit/McpChannelVoice/{VoiceSettingsBindingTests,TranscriptDispatcherTests}.cs`, `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` |
| Modify | `DockerCompose/docker-compose.yml`, `DockerCompose/docker-compose.override.no-dri.yml`, `.vscode/tasks.json`, `CLAUDE.md` |
| Delete | `McpChannelVoice/Services/Stt/WyomingSpeechToText.cs`, `McpChannelVoice/Services/Tts/WyomingTextToSpeech.cs`, `McpChannelVoice/Services/WyomingHealthProbeService.cs` |
| Delete | `Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs`, `Tests/Unit/McpChannelVoice/Tts/WyomingTextToSpeechTests.cs` |
| Delete | `DockerCompose/wyoming-whisper/` (whole directory), compose services `wyoming-whisper`, `piper-voice-fetch`, `wyoming-piper` |

---

### Task 1: `PcmStreamResampler` (stateful 24000→22050 rational resampler)

The highest-risk unit: a phase reset or partial-sample slip at a chunk boundary produces audible clicks in every reply.

**Files:**
- Create: `McpChannelVoice/Services/Tts/PcmStreamResampler.cs`
- Test: `Tests/Unit/McpChannelVoice/Tts/PcmStreamResamplerTests.cs`

**Interfaces:**
- Consumes: nothing (pure class).
- Produces: `public sealed class PcmStreamResampler` with ctor `PcmStreamResampler(int inputRateHz, int outputRateHz)` and `public byte[] Process(ReadOnlySpan<byte> pcm)` — input must be whole 16-bit LE mono samples (even byte count, else `ArgumentException`); returns resampled 16-bit LE mono bytes (possibly empty). State (fractional phase + previous sample) carries across calls; one instance per audio stream, never shared. Task 2 depends on this exact signature.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/Tts/PcmStreamResamplerTests.cs`:

```csharp
using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class PcmStreamResamplerTests
{
    private static byte[] Sine24k(int samples, double freqHz, double amplitude)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * freqHz * i / 24000.0));
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    private static short[] ToSamples(byte[] pcm) =>
        Enumerable.Range(0, pcm.Length / 2)
            .Select(i => (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)))
            .ToArray();

    [Fact]
    public void Process_WholeBuffer_ProducesRationalRatioLength()
    {
        var input = Sine24k(1600, 440, 8000);

        var output = new PcmStreamResampler(24000, 22050).Process(input);

        // 1600 samples * 147/160 = 1470
        (output.Length / 2).ShouldBeInRange(1468, 1472);
        (output.Length % 2).ShouldBe(0);
    }

    [Fact]
    public void Process_ChunkedAtArbitraryBoundaries_MatchesWholeBufferExactly()
    {
        var input = Sine24k(4800, 440, 8000);
        var whole = new PcmStreamResampler(24000, 22050).Process(input);

        var chunked = new PcmStreamResampler(24000, 22050);
        var collected = new List<byte>();
        var offsets = new[] { 0, 2, 36, 1038, 1040, 2400, 4802, 9600 }; // even byte offsets
        for (var i = 0; i < offsets.Length - 1; i++)
        {
            collected.AddRange(chunked.Process(input.AsSpan(offsets[i]..offsets[i + 1])));
        }
        collected.AddRange(chunked.Process(input.AsSpan(offsets[^1]..)));

        collected.ToArray().ShouldBe(whole);
    }

    [Fact]
    public void Process_SineAcrossChunkBoundaries_HasNoDiscontinuity()
    {
        // A click at a chunk boundary is an adjacent-sample jump far above the sine's
        // max slope (amplitude * 2π * f / rate ≈ 1003 for 8000 @ 440 Hz / 22050).
        var input = Sine24k(4800, 440, 8000);
        var resampler = new PcmStreamResampler(24000, 22050);
        var collected = new List<byte>();
        for (var offset = 0; offset < input.Length; offset += 500)
        {
            var end = Math.Min(offset + 500, input.Length);
            collected.AddRange(resampler.Process(input.AsSpan(offset..end)));
        }

        var samples = ToSamples(collected.ToArray());
        var maxDelta = Enumerable.Range(1, samples.Length - 1)
            .Max(i => Math.Abs(samples[i] - samples[i - 1]));
        maxDelta.ShouldBeLessThan(1200);
    }

    [Fact]
    public void Process_ResampledSine_TracksIdealWaveform()
    {
        // Output sample k sits at time k/22050 s, so it must match the ideal sine there;
        // linear interpolation error for 440 Hz @ 24 kHz is only a few LSBs.
        var input = Sine24k(4800, 440, 8000);

        var samples = ToSamples(new PcmStreamResampler(24000, 22050).Process(input));

        var maxError = samples
            .Select((s, i) => Math.Abs(s - 8000 * Math.Sin(2 * Math.PI * 440 * i / 22050.0)))
            .Max();
        maxError.ShouldBeLessThan(240.0);
    }

    [Fact]
    public void Process_OddByteCount_Throws()
    {
        var resampler = new PcmStreamResampler(24000, 22050);

        Should.Throw<ArgumentException>(() => resampler.Process(new byte[3]));
    }
}
```

Note: `Process_SineAcrossChunkBoundaries_HasNoDiscontinuity` splits at `offset += 500`, i.e. every 250 samples. Since 250 is not a multiple of the 160-unit output stride, successive chunks begin at different phase offsets — which is exactly what exercises the carried phase and previous-sample state. (Every split is sample-aligned; `Process` rejects odd lengths, so mid-sample splits are the TTS client's problem, not the resampler's.)

- [ ] **Step 2: Run tests to verify they fail (RED — capture the output)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PcmStreamResamplerTests"`
Expected: compile error `CS0246: The type or namespace name 'PcmStreamResampler' could not be found`.

- [ ] **Step 3: Implement `PcmStreamResampler`**

Create `McpChannelVoice/Services/Tts/PcmStreamResampler.cs`:

```csharp
using System.Buffers.Binary;

namespace McpChannelVoice.Services.Tts;

// Stateful rational resampler for 16-bit LE mono PCM. 24000→22050 reduces to exactly 147/160,
// so phase is tracked in integer units (one input sample = outputRate/gcd units, one output
// sample = inputRate/gcd units) and can never drift. Linear interpolation between the previous
// and current input sample; both the previous sample and the fractional phase survive across
// Process calls, so chunk boundaries introduce no discontinuities (the click regression).
// One instance per audio stream — state is per-utterance, never share across streams/threads.
public sealed class PcmStreamResampler
{
    private readonly int _phasePerInput;
    private readonly int _phasePerOutput;
    private int _phase;
    private short _prev;
    private bool _hasPrev;

    public PcmStreamResampler(int inputRateHz, int outputRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRateHz);
        var gcd = (int)System.Numerics.BigInteger.GreatestCommonDivisor(inputRateHz, outputRateHz);
        _phasePerInput = outputRateHz / gcd;
        _phasePerOutput = inputRateHz / gcd;
    }

    public byte[] Process(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % 2 != 0)
        {
            throw new ArgumentException("PCM input must contain whole 16-bit samples", nameof(pcm));
        }

        var output = new byte[(pcm.Length / 2 * _phasePerInput / _phasePerOutput + 2) * 2];
        var written = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var cur = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            if (!_hasPrev)
            {
                _prev = cur;
                _hasPrev = true;
                continue;
            }
            while (_phase < _phasePerInput)
            {
                var frac = (double)_phase / _phasePerInput;
                var sample = (short)Math.Round(_prev + (cur - _prev) * frac);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(written), sample);
                written += 2;
                _phase += _phasePerOutput;
            }
            _phase -= _phasePerInput;
            _prev = cur;
        }
        return output[..written];
    }
}
```

(No trailing newline. Traditional loops are correct here: hot audio path, per the dotnet-style rule's perf exception.)

- [ ] **Step 4: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PcmStreamResamplerTests"`
Expected: `Passed! - 5 tests`.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Tts/PcmStreamResampler.cs Tests/Unit/McpChannelVoice/Tts/PcmStreamResamplerTests.cs
git commit -m "feat(voice): stateful 147/160 rational PCM resampler for 24k->22.05k TTS"
```

---
### Task 2: `OpenAiTextToSpeech` — streaming Kokoro TTS client

Additive: introduces `OpenAiTtsConfig` alongside the (still-live) Wyoming config so every task leaves the build green. Wyoming code is deleted in Task 6.

**Files:**
- Modify: `McpChannelVoice/Settings/TtsSettings.cs`
- Create: `McpChannelVoice/Services/Tts/OpenAiTextToSpeech.cs`
- Test: `Tests/Unit/McpChannelVoice/Tts/OpenAiTextToSpeechTests.cs`

**Interfaces:**
- Consumes: `PcmStreamResampler(24000, 22050).Process(ReadOnlySpan<byte>)` from Task 1; existing `ITextToSpeech` (`IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, SynthesisOptions options, CancellationToken ct)`), `AudioChunk { Data, Format }`, `AudioFormat`.
- Produces: `public sealed class OpenAiTextToSpeech(HttpClient http, OpenAiTtsConfig config, ILogger<OpenAiTextToSpeech> logger) : ITextToSpeech` and `public record OpenAiTtsConfig { string BaseUrl = "http://mcp-lemonade:13305/v1"; string Model = "kokoro-v1"; string? Voice = "ef_dora"; double Speed = 1.0; int TrailingSilenceTrimThreshold = 500 }`. Tasks 5–6 wire/depend on these exact names.

- [ ] **Step 1: Add `OpenAiTtsConfig` to `TtsSettings.cs`**

Replace the whole file `McpChannelVoice/Settings/TtsSettings.cs` with:

```csharp
namespace McpChannelVoice.Settings;

public record TtsSettings
{
    public WyomingTtsConfig Wyoming { get; init; } = new();
    public OpenAiTtsConfig OpenAi { get; init; } = new();
}

public record OpenAiTtsConfig
{
    public string BaseUrl { get; init; } = "http://mcp-lemonade:13305/v1";
    public string Model { get; init; } = "kokoro-v1";

    // Kokoro voice id. es-419 Spanish voices: ef_dora (female), em_alex, em_santa.
    // Castilian quality is deliberately out of scope for this migration.
    public string? Voice { get; init; } = "ef_dora";
    public double Speed { get; init; } = 1.0;

    // Per-sample int16 amplitude below which tail audio is treated as silence and trimmed from each
    // synthesized utterance, tightening the gap before the follow-up beep. 0 disables trimming.
    public int TrailingSilenceTrimThreshold { get; init; } = 500;
}

public record WyomingTtsConfig
{
    public string Host { get; init; } = "wyoming-piper";
    public int Port { get; init; } = 10200;
    public string? Voice { get; init; }

    // Per-sample int16 amplitude below which tail audio is treated as silence and trimmed from each
    // synthesized utterance, tightening the gap before the follow-up beep. 0 disables trimming.
    public int TrailingSilenceTrimThreshold { get; init; } = 500;
}
```

- [ ] **Step 2: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/Tts/OpenAiTextToSpeechTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class OpenAiTextToSpeechTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    // Serves one scripted byte[] segment per Read call so the test controls exactly how the
    // PCM body is sliced across reads (including mid-sample splits).
    private sealed class ScriptedStream(IReadOnlyList<byte[]> segments) : Stream
    {
        private int _next;

        public bool Exhausted => _next >= segments.Count;

        public override int Read(Span<byte> destination)
        {
            if (_next >= segments.Count)
            {
                return 0;
            }
            var segment = segments[_next++];
            segment.CopyTo(destination);
            return segment.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Ramp24k(int samples) =>
        Enumerable.Range(0, samples)
            .SelectMany(i =>
            {
                var value = (short)(i * 37 - 4000);
                return new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
            })
            .ToArray();

    private static OpenAiTextToSpeech Sut(HttpMessageHandler handler, OpenAiTtsConfig? config = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            config ?? new OpenAiTtsConfig(),
            NullLogger<OpenAiTextToSpeech>.Instance);

    private static HttpResponseMessage PcmResponse(ScriptedStream stream) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    [Fact]
    public async Task SynthesizeAsync_ChunkedPcmWithOddSplits_YieldsResampledAudioMatchingWholeBuffer()
    {
        var pcm = Ramp24k(400); // 800 bytes
        // Odd split: first segment ends mid-sample; the odd-byte carry must reassemble it.
        var stream = new ScriptedStream([pcm[..301], pcm[301..]]);
        var sut = Sut(new StubHandler(_ => PcmResponse(stream)));

        var collected = new List<byte>();
        var formats = new List<AudioFormat>();
        await foreach (var chunk in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
            collected.AddRange(chunk.Data.ToArray());
            formats.Add(chunk.Format);
        }

        var expected = new PcmStreamResampler(24000, 22050).Process(pcm);
        collected.ToArray().ShouldBe(expected);
        formats.ShouldAllBe(f => f.SampleRateHz == 22050 && f.SampleWidthBytes == 2 && f.Channels == 1);
    }

    [Fact]
    public async Task SynthesizeAsync_FirstChunk_ArrivesBeforeBodyCompletes()
    {
        var pcm = Ramp24k(400);
        var stream = new ScriptedStream([pcm[..300], pcm[300..600], pcm[600..]]);
        var sut = Sut(new StubHandler(_ => PcmResponse(stream)));

        await using var enumerator = sut
            .SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None)
            .GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        // Streaming proof: audio was yielded while later segments were still unserved.
        stream.Exhausted.ShouldBeFalse();
    }

    [Fact]
    public async Task SynthesizeAsync_SendsOpenAiSpeechRequest()
    {
        var handler = new StubHandler(_ => PcmResponse(new ScriptedStream([Ramp24k(160)])));
        var sut = Sut(handler, new OpenAiTtsConfig { Voice = "ef_dora", Speed = 1.0 });

        await foreach (var _ in sut.SynthesizeAsync("hola mundo", new SynthesisOptions(), CancellationToken.None))
        {
        }

        handler.LastUri!.ToString().ShouldBe("http://mcp-lemonade:13305/v1/audio/speech");
        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        body["model"]!.GetValue<string>().ShouldBe("kokoro-v1");
        body["input"]!.GetValue<string>().ShouldBe("hola mundo");
        body["voice"]!.GetValue<string>().ShouldBe("ef_dora");
        body["speed"]!.GetValue<double>().ShouldBe(1.0);
        body["response_format"]!.GetValue<string>().ShouldBe("pcm");
        body["stream_format"]!.GetValue<string>().ShouldBe("audio");
    }

    [Fact]
    public async Task SynthesizeAsync_OptionsVoice_OverridesConfigVoice()
    {
        var handler = new StubHandler(_ => PcmResponse(new ScriptedStream([Ramp24k(160)])));
        var sut = Sut(handler, new OpenAiTtsConfig { Voice = "ef_dora" });

        await foreach (var _ in sut.SynthesizeAsync(
            "hola", new SynthesisOptions { Voice = "em_alex" }, CancellationToken.None))
        {
        }

        JsonNode.Parse(handler.LastBody!)!["voice"]!.GetValue<string>().ShouldBe("em_alex");
    }

    [Fact]
    public async Task SynthesizeAsync_Non2xx_ThrowsBeforeYieldingAudio()
    {
        var sut = Sut(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        }));

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task SynthesizeAsync_MidStreamFailure_SurfacesAfterEarlierChunks()
    {
        var pcm = Ramp24k(400);
        var stream = new ThrowingAfterFirstReadStream(pcm[..400]);
        var sut = Sut(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));

        var yielded = 0;
        await Should.ThrowAsync<IOException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
            {
                yielded++;
            }
        });
        yielded.ShouldBe(1);
    }

    private sealed class ThrowingAfterFirstReadStream(byte[] first) : Stream
    {
        private bool _served;

        public override int Read(Span<byte> destination)
        {
            if (_served)
            {
                throw new IOException("connection reset");
            }
            _served = true;
            first.CopyTo(destination);
            return first.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 3: Run tests to verify they fail (RED — capture the output)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiTextToSpeechTests"`
Expected: compile error `CS0246: The type or namespace name 'OpenAiTextToSpeech' could not be found`.

- [ ] **Step 4: Implement `OpenAiTextToSpeech`**

Create `McpChannelVoice/Services/Tts/OpenAiTextToSpeech.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tts;

// Streams Kokoro synthesis from Lemonade's OpenAI-compatible /audio/speech endpoint.
// stream_format=audio + response_format=pcm returns raw 24 kHz mono s16le incrementally
// (the Kokoros backend synthesizes ~10-word chunks and Lemonade forwards them unbuffered),
// so audio starts playing before the whole utterance is synthesized. Each block is resampled
// to the satellites' fixed 22 050 Hz sink. Raw reads are not 2-byte aligned: a 0/1-byte
// remainder is carried between reads so a partial int16 never reaches the resampler.
public sealed class OpenAiTextToSpeech(
    HttpClient http,
    OpenAiTtsConfig config,
    ILogger<OpenAiTextToSpeech> logger) : ITextToSpeech
{
    private const int SourceRateHz = 24000;
    private static readonly AudioFormat _outputFormat = new()
    {
        SampleRateHz = 22050,
        SampleWidthBytes = 2,
        Channels = 1
    };

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = config.Model,
            ["input"] = text,
            ["voice"] = options.Voice ?? config.Voice,
            ["speed"] = config.Speed,
            ["response_format"] = "pcm",
            ["stream_format"] = "audio"
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        // ResponseHeadersRead + incremental reads keep the response streaming end to end; a non-2xx
        // throws before any audio is yielded so the playback loop's onError/OnFailed path fires.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var resampler = new PcmStreamResampler(SourceRateHz, _outputFormat.SampleRateHz);
        var buffer = new byte[8192];
        var carried = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carried, buffer.Length - carried), ct);
            if (read == 0)
            {
                break;
            }

            var available = carried + read;
            var whole = available & ~1;
            var resampled = resampler.Process(buffer.AsSpan(0, whole));

            carried = available - whole;
            if (carried > 0)
            {
                buffer[0] = buffer[whole];
            }

            if (resampled.Length > 0)
            {
                yield return new AudioChunk { Data = resampled, Format = _outputFormat };
            }
        }

        if (carried > 0)
        {
            logger.LogWarning("Kokoro PCM stream ended mid-sample; dropped trailing byte");
        }
        logger.LogDebug("Kokoro synthesis complete");
    }
}
```

- [ ] **Step 5: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiTextToSpeechTests"`
Expected: `Passed! - 6 tests`. Also run `dotnet build agent.sln` — the added `OpenAi` property must not break anything (it's additive).

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Settings/TtsSettings.cs McpChannelVoice/Services/Tts/OpenAiTextToSpeech.cs Tests/Unit/McpChannelVoice/Tts/OpenAiTextToSpeechTests.cs
git commit -m "feat(voice): streaming OpenAI/Kokoro TTS client with odd-byte carry and 22050 resample"
```

---

### Task 3: `OpenAiSpeechToText` — WAV-upload transcription client with verbose_json signals

**Files:**
- Modify: `McpChannelVoice/Settings/SttSettings.cs`
- Create: `McpChannelVoice/Services/Stt/OpenAiSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/OpenAiSpeechToTextTests.cs`

**Interfaces:**
- Consumes: existing `ISpeechToText` (`Task<TranscriptionResult> TranscribeAsync(IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)`), `TranscriptionResult { Text, Language, Confidence, AvgLogProb, NoSpeechProb, CompressionRatio }`, and the tolerant JSON-number helper `WyomingNumber.ReadDouble(JsonObject, string)` (internal, `McpChannelVoice.Services.WyomingProtocol` — part of the kept transport code).
- Produces: `public sealed class OpenAiSpeechToText(HttpClient http, OpenAiSttConfig config, ILogger<OpenAiSpeechToText> logger) : ISpeechToText` and `public record OpenAiSttConfig { string BaseUrl = "http://mcp-lemonade:13305/v1"; string Model = "Whisper-Medium"; string? Language; double AvgLogProbThreshold = -1.0; double NoSpeechProbThreshold = 0.6 }`. Tasks 4–6 depend on these exact names.

- [ ] **Step 1: Add `OpenAiSttConfig` to `SttSettings.cs`**

Replace the whole file `McpChannelVoice/Settings/SttSettings.cs` with:

```csharp
namespace McpChannelVoice.Settings;

public record SttSettings
{
    public WyomingSttConfig Wyoming { get; init; } = new();
    public OpenAiSttConfig OpenAi { get; init; } = new();
    public SegmentedSttConfig Streaming { get; init; } = new();
}

public record OpenAiSttConfig
{
    public string BaseUrl { get; init; } = "http://mcp-lemonade:13305/v1";

    // Lemonade catalog name. The cpu and gpu tiers run the same whisper.cpp engine on the same
    // model (only the device flips), so STT_BACKEND never changes this — it is a container-side
    // concern. Override only to trade accuracy for speed (Whisper-Small) or the reverse
    // (Whisper-Large-v3 / Whisper-Large-v3-Turbo).
    public string Model { get; init; } = "Whisper-Medium";
    public string? Language { get; init; }

    // Gibberish gate: drop transcripts whose avg_logprob falls below the floor or whose
    // no_speech_prob rises above the ceiling. Null signals fail open (TranscriptDispatcher).
    public double AvgLogProbThreshold { get; init; } = -1.0;
    public double NoSpeechProbThreshold { get; init; } = 0.6;
}

public record WyomingSttConfig
{
    public string Host { get; init; } = "wyoming-whisper";
    public int Port { get; init; } = 10300;
    public string? Language { get; init; }
}

public record SegmentedSttConfig
{
    public bool Enabled { get; init; }
    public double SilenceRmsThreshold { get; init; } = 500;
    public int SegmentSilenceMs { get; init; } = 350;
    public int MinSegmentMs { get; init; } = 800;
    public int MaxInFlightDecodes { get; init; } = 1;
}
```

- [ ] **Step 2: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/Stt/OpenAiSpeechToTextTests.cs`:

```csharp
using System.Net;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class OpenAiSpeechToTextTests
{
    // Captures the multipart form structurally (field name → string value, plus the file part)
    // instead of matching substrings against the serialized body — raw-substring assertions like
    // ShouldContain("es") match trivially inside header text ("charset").
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public Dictionary<string, string> Fields { get; } = [];
        public string? FileName { get; private set; }
        public byte[]? FileBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var disposition = part.Headers.ContentDisposition!;
                    if (disposition.FileName is { } fileName)
                    {
                        FileName = fileName.Trim('"');
                        FileBytes = await part.ReadAsByteArrayAsync(ct);
                    }
                    else
                    {
                        Fields[disposition.Name!.Trim('"')] = await part.ReadAsStringAsync(ct);
                    }
                }
            }
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static OpenAiSpeechToText Sut(HttpMessageHandler handler, OpenAiSttConfig? config = null) =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            config ?? new OpenAiSttConfig { Language = "es" },
            NullLogger<OpenAiSpeechToText>.Instance);

    private static async IAsyncEnumerable<AudioChunk> Chunks(params byte[][] payloads)
    {
        foreach (var payload in payloads)
        {
            yield return new AudioChunk
            {
                Data = payload,
                Format = AudioFormat.WyomingStandard,
                Timestamp = TimeSpan.Zero
            };
            await Task.Yield();
        }
    }

    [Fact]
    public async Task TranscribeAsync_VerboseJson_ParsesTextAndDurationWeightedSignals()
    {
        // Weighted by segment duration: avg_logprob (1*-0.2 + 3*-0.8)/4 = -0.65,
        // no_speech_prob (1*0.1 + 3*0.3)/4 = 0.25.
        var sut = Sut(new StubHandler(_ => Json("""
        {
          "task": "transcribe", "language": "es", "duration": 4.0, "text": "hola mundo",
          "segments": [
            { "id": 0, "start": 0.0, "end": 1.0, "text": "hola", "avg_logprob": -0.2, "no_speech_prob": 0.1 },
            { "id": 1, "start": 1.0, "end": 4.0, "text": "mundo", "avg_logprob": -0.8, "no_speech_prob": 0.3 }
          ]
        }
        """)));

        var result = await sut.TranscribeAsync(
            Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("hola mundo");
        result.Language.ShouldBe("es");
        result.AvgLogProb!.Value.ShouldBe(-0.65, 1e-9);
        result.NoSpeechProb!.Value.ShouldBe(0.25, 1e-9);
        result.Confidence.ShouldBeNull();
        result.CompressionRatio.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_PlainJsonBody_FailsOpenWithNullSignals()
    {
        var sut = Sut(new StubHandler(_ => Json("""{ "text": "hola" }""")));

        var result = await sut.TranscribeAsync(
            Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("hola");
        result.Language.ShouldBeNull();
        result.AvgLogProb.ShouldBeNull();
        result.NoSpeechProb.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_SendsWavMultipartWithModelFormatAndLanguage()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler);
        var audio = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        await sut.TranscribeAsync(
            Chunks(audio[..16], audio[16..]), new TranscriptionOptions(), CancellationToken.None);

        handler.LastUri!.ToString().ShouldBe("http://mcp-lemonade:13305/v1/audio/transcriptions");
        handler.FileName.ShouldBe("utterance.wav");
        handler.Fields["model"].ShouldBe("Whisper-Medium");   // config default
        handler.Fields["response_format"].ShouldBe("verbose_json");
        handler.Fields["language"].ShouldBe("es");

        var wav = handler.FileBytes!;
        System.Text.Encoding.ASCII.GetString(wav[..4]).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav[8..12]).ShouldBe("WAVE");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // mono
        BitConverter.ToInt32(wav, 24).ShouldBe(16000);         // incoming satellite rate
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);     // 16-bit
        BitConverter.ToInt32(wav, 40).ShouldBe(32);            // data length
        wav[44..76].ShouldBe(audio);                           // both chunks concatenated
    }

    [Fact]
    public async Task TranscribeAsync_ConfiguredModel_OverridesDefault()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler, new OpenAiSttConfig { Model = "Whisper-Large-v3-Turbo" });

        await sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None);

        handler.Fields["model"].ShouldBe("Whisper-Large-v3-Turbo");
    }

    [Fact]
    public async Task TranscribeAsync_OptionsLanguage_OverridesConfigLanguage()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hello" }"""));
        var sut = Sut(handler);

        await sut.TranscribeAsync(
            Chunks(new byte[32]), new TranscriptionOptions { Language = "en" }, CancellationToken.None);

        handler.Fields["language"].ShouldBe("en");
    }

    [Fact]
    public async Task TranscribeAsync_EmptyAudio_ReturnsEmptyWithoutHttpCall()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "ghost" }"""));
        var sut = Sut(handler);

        var result = await sut.TranscribeAsync(Chunks(), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("");
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task TranscribeAsync_Non2xx_Throws()
    {
        var sut = Sut(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        }));

        await Should.ThrowAsync<HttpRequestException>(() =>
            sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_BodyWithoutText_Throws()
    {
        var sut = Sut(new StubHandler(_ => Json("""{ "status": "ok" }""")));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None));
    }

}
```

- [ ] **Step 3: Run tests to verify they fail (RED — capture the output)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToTextTests"`
Expected: compile error `CS0246: The type or namespace name 'OpenAiSpeechToText' could not be found`.

- [ ] **Step 4: Implement `OpenAiSpeechToText`**

Create `McpChannelVoice/Services/Stt/OpenAiSpeechToText.cs`:

```csharp
using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

// Transcribes one utterance segment via Lemonade's OpenAI-compatible /audio/transcriptions
// endpoint. The segment is buffered into a WAV blob (mono s16le at the incoming rate — the
// satellites send 16 kHz) and posted as multipart with response_format=verbose_json so the
// per-segment avg_logprob / no_speech_prob quality signals reach the gibberish gate. The
// signals are duration-weighted across the body's segments (one POST usually carries one, but
// whisper may split); a body without segments (plain json shape) degrades to null signals and
// the gate fails open. Lemonade emits neither score nor compression_ratio — left null.
public sealed class OpenAiSpeechToText(
    HttpClient http,
    OpenAiSttConfig config,
    ILogger<OpenAiSpeechToText> logger) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            chunks.Add(chunk);
        }

        var dataBytes = chunks.Sum(c => c.Data.Length);
        if (dataBytes == 0)
        {
            return new TranscriptionResult { Text = "" };
        }

        using var content = new MultipartFormDataContent();
        var wav = new ByteArrayContent(BuildWav(chunks, chunks[0].Format, dataBytes));
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(wav, "file", "utterance.wav");
        content.Add(new StringContent(config.Model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        if ((options.Language ?? config.Language) is { } language)
        {
            content.Add(new StringContent(language), "language");
        }

        using var response = await http.PostAsync(
            $"{config.BaseUrl.TrimEnd('/')}/audio/transcriptions", content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        if (JsonNode.Parse(body) is not JsonObject json || json["text"] is null)
        {
            throw new InvalidOperationException("Malformed transcription response from Lemonade");
        }

        var result = ParseResult(json);
        logger.LogInformation(
            "Lemonade transcript: text={Text} lang={Lang} avg_logprob={AvgLogProb} no_speech_prob={NoSpeechProb}",
            result.Text, result.Language, result.AvgLogProb, result.NoSpeechProb);
        return result;
    }

    private static TranscriptionResult ParseResult(JsonObject json)
    {
        var weighted = ((json["segments"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(s => (
                Weight: Math.Max(
                    (WyomingNumber.ReadDouble(s, "end") ?? 0) - (WyomingNumber.ReadDouble(s, "start") ?? 0),
                    1e-9),
                Segment: s))
            .ToList();

        return new TranscriptionResult
        {
            Text = json["text"]?.GetValue<string>() ?? string.Empty,
            Language = json["language"]?.GetValue<string>(),
            AvgLogProb = WeightedMean(weighted, s => WyomingNumber.ReadDouble(s, "avg_logprob")),
            NoSpeechProb = WeightedMean(weighted, s => WyomingNumber.ReadDouble(s, "no_speech_prob"))
        };
    }

    // Segments differ in length, so a plain mean would let a short noise segment outvote long
    // clean speech. Weight by duration; segments without the value abstain (fail-open).
    private static double? WeightedMean(
        IReadOnlyList<(double Weight, JsonObject Segment)> weighted,
        Func<JsonObject, double?> selector)
    {
        var pairs = weighted
            .Where(w => selector(w.Segment) is not null)
            .Select(w => (w.Weight, Value: selector(w.Segment)!.Value))
            .ToList();
        return pairs.Count > 0
            ? pairs.Sum(p => p.Weight * p.Value) / pairs.Sum(p => p.Weight)
            : null;
    }

    private static byte[] BuildWav(IReadOnlyList<AudioChunk> chunks, AudioFormat format, int dataBytes)
    {
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE", span[8..]);
        Encoding.ASCII.GetBytes("fmt ", span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], format.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(
            span[28..], format.SampleRateHz * format.SampleWidthBytes * format.Channels);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(format.SampleWidthBytes * format.Channels));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)(format.SampleWidthBytes * 8));
        Encoding.ASCII.GetBytes("data", span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        var offset = 44;
        foreach (var chunk in chunks)
        {
            chunk.Data.Span.CopyTo(span[offset..]);
            offset += chunk.Data.Length;
        }
        return wav;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToTextTests"`
Expected: `Passed! - 8 tests`. Also `dotnet build agent.sln` stays green.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Settings/SttSettings.cs McpChannelVoice/Services/Stt/OpenAiSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/OpenAiSpeechToTextTests.cs
git commit -m "feat(voice): OpenAI/Lemonade STT client with verbose_json quality signals"
```

---
### Task 4: `TranscriptDispatcher` gate repoint (Confidence → AvgLogProb / NoSpeechProb)

Lemonade supplies no `score`, so `Confidence` will always be null. The gate must threshold on `AvgLogProb` (floor) and `NoSpeechProb` (ceiling) instead, keeping the fail-open-on-null behavior. `ConfigModule` constructs the dispatcher, so its registration is updated in the same task to keep the build green.

**Files:**
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs` (lines 8–31: ctor + gate)
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (the `AddSingleton<TranscriptDispatcher>` registration, currently lines 55–61)
- Modify: `Domain/DTOs/Voice/TranscriptionResult.cs` (comment only)
- Test: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`
- Test: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` (six ctor call sites — **required**, see Step 1.5; `Tests/Unit` and `Tests/Integration` are one project, so missing these breaks every later build/test gate)

**Interfaces:**
- Consumes: `OpenAiSttConfig.AvgLogProbThreshold` / `.NoSpeechProbThreshold` from Task 3 (`settings.Stt.OpenAi`).
- Produces: new ctor `TranscriptDispatcher(ChannelNotificationEmitter emitter, IMetricsPublisher publisher, VoiceConversationManager manager, double avgLogProbThreshold, double noSpeechProbThreshold, TimeProvider timeProvider, ILogger<TranscriptDispatcher> logger)`. `DispatchAsync` signature unchanged.

- [ ] **Step 1: Update the tests (failing first)**

In `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`:

1. In `Build()`, replace `confidenceThreshold: 0.5,` with:

```csharp
            avgLogProbThreshold: -1.0, noSpeechProbThreshold: 0.6,
```

2. The three inline `new TranscriptDispatcher(...)` constructions — lines 131, 185 and 227, in `DispatchAsync_EmptyText_DropsAndPublishesDroppedMetric`, `DispatchAsync_Dispatched_PublishesCaptureAndWhisperStats` and `DispatchAsync_Dropped_PublishesCaptureAndWhisperStats` — replace their `confidenceThreshold: 0.5,` line the same way.

3. Replace the test `DispatchAsync_LowConfidence_DoesNotOpenConversation` with these three:

```csharp
    [Fact]
    public async Task DispatchAsync_LowAvgLogProb_DoesNotOpenConversation()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "mumble", AvgLogProb = -2.1 }, "agent-1", null, default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_HighNoSpeechProb_DoesNotOpenConversation()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "ffff", NoSpeechProb = 0.8 }, "agent-1", null, default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_NullQualitySignals_FailsOpenAndDispatches()
    {
        var (sut, manager, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "sin señales" }, "agent-1", null, default);

        ok.ShouldBeTrue();
        manager.GetActiveConversationId("kitchen-01").ShouldNotBeNull();
        emitter.Captured.Count.ShouldBe(1);
    }
```

4. Add one boundary test alongside them:

```csharp
    [Fact]
    public async Task DispatchAsync_SignalsWithinThresholds_Dispatches()
    {
        var (sut, _, emitter) = Build();

        var ok = await sut.DispatchAsync(
            Session(),
            new TranscriptionResult { Text = "hola", AvgLogProb = -0.3, NoSpeechProb = 0.1 },
            "agent-1", null, default);

        ok.ShouldBeTrue();
        emitter.Captured.Count.ShouldBe(1);
    }
```

Note: `DispatchAsync_Dropped_PublishesCaptureAndWhisperStats` already passes `AvgLogProb = -2.1` (below the -1.0 floor), so it still drops under the new gate — leave its transcript values as they are. All other existing tests pass `Confidence` only, which the new gate ignores → they dispatch (fail-open), matching their current expectations.

5. **`Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` constructs `TranscriptDispatcher` at six sites** — lines **135, 275, 394, 497, 604, 718** — each passing one positional threshold. `Tests/Unit` and `Tests/Integration` are the same csproj, so leaving these breaks the entire Tests build and every `dotnet build`/`dotnet test` gate from here through Task 9. At each site replace the single `0.4` argument with the two new thresholds:

```csharp
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, manager, -1.0, 0.6, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
```

Four sites (275, 394, 497, and one of the others) are written as a single line and three are wrapped across two lines; match whichever local formatting is already there rather than reflowing. Verify none remain:

```bash
grep -n "new TranscriptDispatcher(" -A2 Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs | grep -c "manager, 0.4"
```
Expected: `0`.

- [ ] **Step 2: Run tests to verify they fail (RED — capture the output)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: compile error — `TranscriptDispatcher` has no parameter named `avgLogProbThreshold` yet.

- [ ] **Step 3: Repoint the gate**

In `McpChannelVoice/Services/TranscriptDispatcher.cs`, change the primary constructor:

```csharp
public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    VoiceConversationManager manager,
    double avgLogProbThreshold,
    double noSpeechProbThreshold,
    TimeProvider timeProvider,
    ILogger<TranscriptDispatcher> logger)
```

and replace the gate block (currently lines 23–31):

```csharp
        // Lemonade emits no whisper score, so Confidence is never populated; the gibberish gate
        // thresholds the raw quality signals instead. Null signals fail open — a backend that
        // stops emitting them degrades to dispatch-everything, never to drop-everything.
        var lowQuality = (transcript.AvgLogProb is { } lp && lp < avgLogProbThreshold)
                         || (transcript.NoSpeechProb is { } np && np > noSpeechProbThreshold);
        if (string.IsNullOrWhiteSpace(transcript.Text) || lowQuality)
        {
            logger.LogInformation(
                "Dropping transcript for {Satellite}: empty={Empty} lowQuality={LowQuality} avg_logprob={AvgLogProb} no_speech_prob={NoSpeechProb}",
                session.SatelliteId,
                string.IsNullOrWhiteSpace(transcript.Text),
                lowQuality,
                transcript.AvgLogProb,
                transcript.NoSpeechProb);
```

(The two `VoiceEvent` publications keep passing `Confidence`/`AvgLogProb`/`NoSpeechProb`/`CompressionRatio` through unchanged — the metric schema is untouched.)

In `McpChannelVoice/Modules/ConfigModule.cs`, update the registration:

```csharp
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                settings.Stt.OpenAi.AvgLogProbThreshold,
                settings.Stt.OpenAi.NoSpeechProbThreshold,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()))
```

In `Domain/DTOs/Voice/TranscriptionResult.cs`, replace the comment above `AvgLogProb` (it still describes the patched wyoming-whisper server and Confidence-only gating):

```csharp
    // Raw whisper quality signals, duration-weighted per POST by OpenAiSpeechToText and again
    // across utterance segments by SegmentedSpeechToText. AvgLogProb/NoSpeechProb gate dispatch
    // (TranscriptDispatcher); null means "no signal" and fails open. Confidence and
    // CompressionRatio stay for metrics but Lemonade emits neither (always null).
```

- [ ] **Step 4: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: all pass (12 tests). Also `dotnet build agent.sln` stays green — this only holds if Step 1.5's six integration-test call sites were updated.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/Modules/ConfigModule.cs Domain/DTOs/Voice/TranscriptionResult.cs Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs
git commit -m "feat(voice): repoint gibberish gate to avg_logprob/no_speech_prob thresholds"
```

---

### Task 5: DI cutover + per-satellite override call sites + appsettings

Swap `ISpeechToText`/`ITextToSpeech` to the OpenAI clients, flip every `Stt.Wyoming`/`Tts.Wyoming` read to `Stt.OpenAi`/`Tts.OpenAi`, and rewrite the voice channel's appsettings. Wyoming engine classes still exist after this task (unreferenced); Task 6 deletes them.

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (STT registration lines 74–82, TTS registration lines 89–94, health-probe line 96)
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:277-280`
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs:199`
- Modify: `McpChannelVoice/McpTools/RequestApprovalTool.cs:118` and `:134`
- Modify: `McpChannelVoice/Services/AnnouncementService.cs:58-60`
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs:149`
- Modify: `McpChannelVoice/appsettings.json`
- Modify: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs:146-148`
- Test (new): `Tests/Unit/McpChannelVoice/ConfigModuleTests.cs`

**Interfaces:**
- Consumes: `OpenAiSpeechToText`, `OpenAiTextToSpeech`, `OpenAiSttConfig`, `OpenAiTtsConfig` (Tasks 2–3); existing wrappers `SegmentedSpeechToText.Wrap(inner, SegmentedSttConfig, ILoggerFactory)` and `SilenceTrimmingTextToSpeech.Wrap(inner, int threshold)`.
- Produces: DI resolution of `ISpeechToText` → `SegmentedSpeechToText`-wrapped `OpenAiSpeechToText` (bare when `Streaming.Enabled == false`) and `ITextToSpeech` → `SilenceTrimmingTextToSpeech`-wrapped `OpenAiTextToSpeech` (bare when `TrailingSilenceTrimThreshold == 0`). Per-satellite overrides read `Config.Stt?.OpenAi?.Language` and `Config.Tts?.OpenAi?.Voice`.

- [ ] **Step 1: Write the failing DI tests**

Create `Tests/Unit/McpChannelVoice/ConfigModuleTests.cs`:

```csharp
using Domain.Contracts;
using McpChannelVoice.Modules;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ConfigModuleTests
{
    // Registration-only smoke: nothing here connects to Redis or starts hosted services —
    // resolving the STT/TTS graph must work from settings alone.
    private static ServiceProvider Build(VoiceSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureVoiceChannel(settings);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ConfigureVoiceChannel_StreamingEnabled_ResolvesSegmentedOpenAiSpeechToText()
    {
        using var provider = Build(new VoiceSettings
        {
            Stt = new SttSettings { Streaming = new SegmentedSttConfig { Enabled = true } }
        });

        provider.GetRequiredService<ISpeechToText>().ShouldBeOfType<SegmentedSpeechToText>();
    }

    [Fact]
    public void ConfigureVoiceChannel_StreamingDisabled_ResolvesBareOpenAiSpeechToText()
    {
        using var provider = Build(new VoiceSettings());

        provider.GetRequiredService<ISpeechToText>().ShouldBeOfType<OpenAiSpeechToText>();
    }

    [Fact]
    public void ConfigureVoiceChannel_TrimEnabled_ResolvesSilenceTrimmedOpenAiTextToSpeech()
    {
        using var provider = Build(new VoiceSettings());

        provider.GetRequiredService<ITextToSpeech>().ShouldBeOfType<SilenceTrimmingTextToSpeech>();
    }

    [Fact]
    public void ConfigureVoiceChannel_TrimDisabled_ResolvesBareOpenAiTextToSpeech()
    {
        using var provider = Build(new VoiceSettings
        {
            Tts = new TtsSettings { OpenAi = new OpenAiTtsConfig { TrailingSilenceTrimThreshold = 0 } }
        });

        provider.GetRequiredService<ITextToSpeech>().ShouldBeOfType<OpenAiTextToSpeech>();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (RED — capture the output)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConfigModuleTests"`
Expected: **exactly 2 of the 4 fail** —
- `ConfigureVoiceChannel_StreamingDisabled_ResolvesBareOpenAiSpeechToText` fails reporting **`WyomingSpeechToText`**.
- `ConfigureVoiceChannel_TrimDisabled_ResolvesBareOpenAiTextToSpeech` fails reporting **`SilenceTrimmingTextToSpeech`** (not `WyomingTextToSpeech`): the current code reads `settings.Tts.Wyoming.TrailingSilenceTrimThreshold`, whose default 500 still wraps, ignoring the test's `OpenAi.TrailingSilenceTrimThreshold = 0`.

The other two pass at RED because both wrapper types are backend-agnostic — `SegmentedSpeechToText` and `SilenceTrimmingTextToSpeech` wrap the Wyoming clients today just as they will wrap the OpenAI ones. That is expected, not a problem; the RED evidence is the two failures above.

- [ ] **Step 3: Swap the DI registrations**

In `McpChannelVoice/Modules/ConfigModule.cs`:

1. Replace the `ISpeechToText` registration (lines 74–82) and the `ITextToSpeech` registration (lines 89–94) with:

```csharp
        const string lemonadeHttpClient = "lemonade";
        // Streaming TTS reads can outlive the default 100 s client timeout on long replies;
        // cancellation is driven by the per-turn CancellationToken instead.
        services.AddHttpClient(lemonadeHttpClient)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<ISpeechToText>(sp =>
        {
            var inner = new McpChannelVoice.Services.Stt.OpenAiSpeechToText(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(lemonadeHttpClient),
                settings.Stt.OpenAi,
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.OpenAiSpeechToText>>());

            return McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddSingleton<ITextToSpeech>(sp =>
            McpChannelVoice.Services.Tts.SilenceTrimmingTextToSpeech.Wrap(
                new McpChannelVoice.Services.Tts.OpenAiTextToSpeech(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(lemonadeHttpClient),
                    settings.Tts.OpenAi,
                    sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.OpenAiTextToSpeech>>()),
                settings.Tts.OpenAi.TrailingSilenceTrimThreshold));
```

This single block replaces the old `ISpeechToText` registration in place; delete the old `ITextToSpeech` registration lines further down. Keep the registrations between and around them (`AddHostedService<WyomingSatelliteHost>()`, `AddSingleton(settings.WyomingClient)`, `ReplyTextAccumulator`) exactly where they are.

2. Delete the line `services.AddHostedService<WyomingHealthProbeService>();` (the service class itself is deleted in Task 6; nothing replaces it — the wyoming health tiles disappear from the dashboard with the containers).

- [ ] **Step 4: Flip the six per-satellite override call sites**

1. `McpChannelVoice/Services/WyomingSatelliteHost.cs` (lines 277–280) — comment and read:

```csharp
            // Honor a per-satellite STT language override (symmetric with the per-satellite
            // Tts.OpenAi.Voice override resolved in SendReplyTool/AnnouncementService); null falls
            // back to the global Stt.OpenAi.Language inside the backend.
            var options = new TranscriptionOptions { Language = session.Config.Stt?.OpenAi?.Language };
```

2. `McpChannelVoice/McpTools/SendReplyTool.cs:199`:

```csharp
        var voice = session.Config.Tts?.OpenAi?.Voice ?? settings.Tts.OpenAi.Voice;
```

3. `McpChannelVoice/McpTools/RequestApprovalTool.cs:118` and `:134` (both `SpeakAsync` and `SpeakAndAwaitAsync`):

```csharp
        var voice = session.Config.Tts?.OpenAi?.Voice ?? settings.Tts.OpenAi.Voice;
```

4. `McpChannelVoice/Services/AnnouncementService.cs:58-60`:

```csharp
            var voice = request.Voice
                        ?? session.Config.Tts?.OpenAi?.Voice
                        ?? settings.Tts.OpenAi.Voice;
```

5. `McpChannelVoice/Services/InsistentAnnouncementController.cs:149`:

```csharp
        var voice = request.Voice ?? settings.Tts.OpenAi.Voice;
```

6. `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` (lines 146–148):

```csharp
                // Per-satellite STT language override must reach the backend (symmetric with the
                // per-satellite Tts.OpenAi.Voice override), not be silently dropped.
                Stt = new SttSettings { OpenAi = new OpenAiSttConfig { Language = "en" } }
```

7. Same file, **line 174** — a trailing comment naming the old config path. The Task 6 and Task 9 greps do not match this string, so it survives the sweep unless fixed here:

```csharp
        capturedLanguage.ShouldBe("en"); // per-satellite Stt.OpenAi.Language threaded into TranscriptionOptions
```

- [ ] **Step 5: Rewrite the voice channel appsettings**

In `McpChannelVoice/appsettings.json`, replace the `Stt` and `Tts` sections (keep everything else, including `ConfidenceThreshold` — it dies in Task 6):

```json
    "Stt": {
        "OpenAi": {
            "BaseUrl": "http://mcp-lemonade:13305/v1",
            "Model": "Whisper-Medium",
            "Language": "es",
            "AvgLogProbThreshold": -1.0,
            "NoSpeechProbThreshold": 0.6
        },
        "Streaming": {
            "Enabled": true,
            "SilenceRmsThreshold": 500,
            "SegmentSilenceMs": 350,
            "MinSegmentMs": 800,
            "MaxInFlightDecodes": 1
        }
    },
    "Tts": {
        "OpenAi": {
            "BaseUrl": "http://mcp-lemonade:13305/v1",
            "Model": "kokoro-v1",
            "Voice": "ef_dora",
            "Speed": 1.0,
            "TrailingSilenceTrimThreshold": 500
        }
    },
```

(`McpChannelVoice/appsettings.Development.json` holds only `Satellites` address overrides — no `Stt`/`Tts` sections — so it needs no change; verify with a quick read.)

- [ ] **Step 6: Run tests to verify they pass (GREEN)**

Run: `dotnet build agent.sln && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice"`
Expected: build green; all `McpChannelVoice` unit tests pass, including the 4 new `ConfigModuleTests`.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/McpTools/SendReplyTool.cs McpChannelVoice/McpTools/RequestApprovalTool.cs McpChannelVoice/Services/AnnouncementService.cs McpChannelVoice/Services/InsistentAnnouncementController.cs McpChannelVoice/appsettings.json Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs Tests/Unit/McpChannelVoice/ConfigModuleTests.cs
git commit -m "feat(voice): cut STT/TTS DI and per-satellite overrides over to Lemonade OpenAI clients"
```

---
### Task 6: Delete the Wyoming engine code, configs, and probe (hard cutover)

Pure removal — everything deleted here became unreferenced in Task 5. Also removes the now-dead `ConfidenceThreshold` and updates the settings-binding tests to the new shape.

**Files:**
- Delete: `McpChannelVoice/Services/Stt/WyomingSpeechToText.cs`
- Delete: `McpChannelVoice/Services/Tts/WyomingTextToSpeech.cs`
- Delete: `McpChannelVoice/Services/WyomingHealthProbeService.cs`
- Delete: `Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs`
- Delete: `Tests/Unit/McpChannelVoice/Tts/WyomingTextToSpeechTests.cs`
- Modify: `McpChannelVoice/Settings/SttSettings.cs` (drop `Wyoming` property + `WyomingSttConfig` record)
- Modify: `McpChannelVoice/Settings/TtsSettings.cs` (drop `Wyoming` property + `WyomingTtsConfig` record)
- Modify: `McpChannelVoice/Settings/VoiceSettings.cs` (drop `ConfidenceThreshold`)
- Modify: `McpChannelVoice/appsettings.json` (drop the `"ConfidenceThreshold": 0.4,` line)
- Test: `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs`

**Interfaces:**
- Consumes: the OpenAI-only settings shape from Tasks 2–3.
- Produces: `SttSettings { OpenAi, Streaming }`, `TtsSettings { OpenAi }`, `VoiceSettings` without `ConfidenceThreshold`. No other type changes.

- [ ] **Step 1: Update the binding tests (failing first)**

In `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs`, in `VoiceSettings_BindsFromJson`:

1. Replace the `"Stt"`/`"Tts"` blocks and drop `"ConfidenceThreshold"` in the JSON literal:

```json
          "Stt": {
            "OpenAi": { "BaseUrl": "http://mcp-lemonade:13305/v1", "Model": "Whisper-Medium", "Language": "es", "AvgLogProbThreshold": -1.2, "NoSpeechProbThreshold": 0.5 }
          },
          "Tts": {
            "OpenAi": { "Voice": "ef_dora", "Speed": 1.1 }
          },
```

2. Replace the corresponding assertions (`settings.Stt.Wyoming.Host...`, `settings.Tts.Wyoming.Voice...`, `settings.ConfidenceThreshold...`) with:

```csharp
        settings.Stt.OpenAi.BaseUrl.ShouldBe("http://mcp-lemonade:13305/v1");
        settings.Stt.OpenAi.Model.ShouldBe("Whisper-Medium");
        settings.Stt.OpenAi.Language.ShouldBe("es");
        settings.Stt.OpenAi.AvgLogProbThreshold.ShouldBe(-1.2);
        settings.Stt.OpenAi.NoSpeechProbThreshold.ShouldBe(0.5);
        settings.Tts.OpenAi.Voice.ShouldBe("ef_dora");
        settings.Tts.OpenAi.Speed.ShouldBe(1.1);
        settings.Tts.OpenAi.Model.ShouldBe("kokoro-v1");
```

Leave every other test in the file untouched (locality, env-var satellite binding, lifetime default).

- [ ] **Step 2: Run the binding tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsBindingTests"`
Expected: **PASS** — the OpenAi config shape has bound since Task 3, so there is no RED here; this is a pure-deletion task and its real gate is the zero-references grep + full suite in Step 4. If this run fails, the Task 3 config shape drifted — stop and fix that before deleting anything.

- [ ] **Step 3: Delete the Wyoming engine pieces**

```bash
git rm McpChannelVoice/Services/Stt/WyomingSpeechToText.cs \
       McpChannelVoice/Services/Tts/WyomingTextToSpeech.cs \
       McpChannelVoice/Services/WyomingHealthProbeService.cs \
       Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs \
       Tests/Unit/McpChannelVoice/Tts/WyomingTextToSpeechTests.cs
```

Then edit the three settings files:

1. `McpChannelVoice/Settings/SttSettings.cs` — remove the `public WyomingSttConfig Wyoming { get; init; } = new();` property and the whole `WyomingSttConfig` record (keep `OpenAiSttConfig` and `SegmentedSttConfig`).
2. `McpChannelVoice/Settings/TtsSettings.cs` — remove the `public WyomingTtsConfig Wyoming { get; init; } = new();` property and the whole `WyomingTtsConfig` record (keep `OpenAiTtsConfig`).
3. `McpChannelVoice/Settings/VoiceSettings.cs` — remove the line `public double ConfidenceThreshold { get; init; } = 0.4;`.
4. `McpChannelVoice/appsettings.json` — remove the line `"ConfidenceThreshold": 0.4,`.

- [ ] **Step 4: Verify nothing references the deleted symbols, then run the suite (GREEN)**

```bash
grep -rn "WyomingSttConfig\|WyomingTtsConfig\|WyomingSpeechToText\|WyomingTextToSpeech\|WyomingHealthProbe\|ConfidenceThreshold" \
  --include="*.cs" --include="*.json" . | grep -v "obj/\|bin/\|satellite/\|docs/"
```
Expected: **zero hits**.

Run: `dotnet build agent.sln && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice"`
Expected: build green, all voice unit tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A McpChannelVoice Tests/Unit/McpChannelVoice
git commit -m "feat(voice): delete Wyoming STT/TTS engine clients, configs, and health probe"
```

---

### Task 7: Lemonade container — Dockerfile, entrypoint, compose, overrides

One custom image maps `STT_BACKEND` → the whisper.cpp device; compose replaces the three Wyoming services with `mcp-lemonade`.

**Files:**
- Create: `DockerCompose/lemonade/Dockerfile`
- Create: `DockerCompose/lemonade/entrypoint.sh`
- Create: `DockerCompose/lemonade/smoke.sh`
- Modify: `DockerCompose/docker-compose.yml` (replace services `wyoming-whisper`/`piper-voice-fetch`/`wyoming-piper` at lines 588–692 with `mcp-lemonade`; edit `mcp-channel-voice` `depends_on`/`environment` at lines 573–586)
- Modify: `DockerCompose/docker-compose.override.no-dri.yml`
- Modify: `.vscode/tasks.json` (SERVICES list, line 6)
- Delete: `DockerCompose/wyoming-whisper/` (Dockerfile, README.md, patch)

**Interfaces:**
- Consumes: nothing from earlier tasks — the hub reaches the container purely over HTTP at the `BaseUrl` already configured in Task 5.
- Produces: compose service `mcp-lemonade` on port 13305 serving `/v1/audio/transcriptions` + `/v1/audio/speech`; env contract `STT_BACKEND` ∈ {`cpu`,`gpu`} (default `gpu`), consumed **only** by the lemonade entrypoint.

- [ ] **Step 1: Confirm the base image's shape**

These values were verified against the shipped `v11.0.0` image config and the v11 source tree, and the rest of this task depends on them. Re-confirm rather than rediscover — if any differs, stop and adjust the Dockerfile, the entrypoint's `CONFIG_DIR`, and the Step 5 volume targets together:

```bash
docker pull ghcr.io/lemonade-sdk/lemonade-server:v11.0.0
docker inspect ghcr.io/lemonade-sdk/lemonade-server:v11.0.0 \
  --format 'entrypoint={{.Config.Entrypoint}} cmd={{.Config.Cmd}} workdir={{.Config.WorkingDir}} user={{.Config.User}}'
docker run --rm --entrypoint sh ghcr.io/lemonade-sdk/lemonade-server:v11.0.0 \
  -c 'echo HOME=$HOME; id; command -v curl; ls /opt/lemonade'
```

Expected: **no `ENTRYPOINT`**, `CMD ["./lemond","--host","0.0.0.0"]`, `WorkingDir=/opt/lemonade`, `User=lemonade` (**UID 10001**), `HOME=/opt/lemonade`, base `ubuntu:24.04`, `curl` present. Consequences already baked into this task:
- Docker starts an entrypoint with CWD = WORKDIR, so `exec ./lemond` resolves. There is no stock entrypoint or init shim to preserve, and `exec` keeps `lemond` as PID 1, so `docker stop` behaves exactly as upstream.
- `$HOME/.cache/lemonade` = `/opt/lemonade/.cache/lemonade`, which **is** the directory Lemonade reads `config.json` from (`path_linux.cpp`; overridable via `LEMONADE_CACHE_DIR`). The entrypoint's default is therefore correct as written.
- **v11 changed the runtime user to unprivileged 10001** (a documented breaking change). Every host path mounted into this container must be writable by 10001 — see Step 5.

- [ ] **Step 2: Write the Dockerfile**

The stock image ships the Vulkan **loader** (`libvulkan1`) but **no driver**: `mesa-vulkan-drivers` (RADV) is absent, and the runtime-downloaded `whisper-*-linux-vulkan` tarball contains only `libggml-vulkan.so`, which consumes the loader rather than providing a device. Worse, Lemonade's `validate_backend_choice("whispercpp","vulkan")` passes on any x86_64, so without RADV the model **loads happily, logs `Using backend: vulkan`, finds zero Vulkan devices, and decodes on CPU** — the silent failure this whole migration is meant to avoid. Install the driver.

Create `DockerCompose/lemonade/Dockerfile`:

```dockerfile
# Lemonade Server: OpenAI-compatible STT (whisper.cpp on cpu or vulkan) + TTS (Kokoro).
# Replaces wyoming-whisper + wyoming-piper. The entrypoint maps STT_BACKEND (cpu|gpu) to
# the whisper.cpp device; both tiers run the same Whisper-Medium model.
# Pin: v11.0.0 (2026-07-15, current latest); bump deliberately, not via :latest.
FROM ghcr.io/lemonade-sdk/lemonade-server:v11.0.0

# The stock image has the Vulkan loader but no ICD, so ggml-vulkan would enumerate zero
# devices and fall back to CPU *without erroring*. RADV is what actually drives the 890M.
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends mesa-vulkan-drivers \
    && rm -rf /var/lib/apt/lists/*
USER lemonade

COPY --chmod=755 entrypoint.sh /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
```

Dropping back to `USER lemonade` matters: v11 runs unprivileged by design, and the entrypoint only needs to write inside `$HOME`, which the image already chowns to that user.

- [ ] **Step 3: Write the entrypoint**

Create `DockerCompose/lemonade/entrypoint.sh` (LF line endings):

```sh
#!/bin/sh
# Maps the single STT_BACKEND env var (cpu|gpu, default gpu) onto Lemonade's whisper.cpp
# device selection (config.json — the same mechanism Lemonade's docker docs use for
# llamacpp) and pre-pulls the model. Both tiers run the same model, so the hub needs no
# corresponding setting; STT_BACKEND is container-side only.
set -eu

BACKEND="${STT_BACKEND:-gpu}"
# Pre-pull target only. Keep in sync with the hub's Stt__OpenAi__Model if you override it;
# a mismatch just means the wrong model is warmed and the right one downloads lazily.
MODEL="${STT_MODEL:-Whisper-Medium}"

case "$BACKEND" in
  cpu)  WHISPER_BACKEND="cpu" ;;
  gpu)  WHISPER_BACKEND="vulkan" ;;
  npu)  echo "STT_BACKEND selects the whisper.cpp device, whose NPU option is Windows-only. The Linux NPU tier goes through Lemonade's separate 'flm' recipe instead: leave STT_BACKEND on cpu or gpu and apply docker-compose.override.npu.yml with STT_MODEL set to an flm-recipe ASR model." >&2
        exit 1 ;;
  *)    echo "Unknown STT_BACKEND '$BACKEND' (expected cpu|gpu)" >&2; exit 1 ;;
esac

CONFIG_DIR="${LEMONADE_CONFIG_DIR:-$HOME/.cache/lemonade}"
mkdir -p "$CONFIG_DIR"
# Dedicated STT/TTS container: whispercpp is the only recipe we configure, so a plain
# overwrite is fine (no llamacpp settings to preserve).
cat > "$CONFIG_DIR/config.json" <<EOF
{
  "whispercpp": { "backend": "$WHISPER_BACKEND" }
}
EOF

echo "lemonade: whispercpp.backend=$WHISPER_BACKEND model=$MODEL"

# Test seam: config-mapping can be verified without starting the server (no GPU, no model pull).
if [ "${STT_CONFIG_ONLY:-0}" = "1" ]; then
  exit 0
fi

# Pre-pull the tier's whisper model once the server is up so the first utterance doesn't
# pay the download; Kokoro (TTS) downloads on first use. Best-effort by design.
(
  i=0
  while [ "$i" -lt 60 ]; do
    sleep 2
    if curl -fsS "http://127.0.0.1:13305/api/v1/health" >/dev/null 2>&1; then
      curl -fsS -X POST "http://127.0.0.1:13305/api/v1/pull" \
        -H "Content-Type: application/json" \
        -d "{\"model_name\": \"$MODEL\"}" >/dev/null 2>&1 || true
      exit 0
    fi
    i=$((i + 1))
  done
) &

exec ./lemond --host 0.0.0.0 --port 13305
```

- [ ] **Step 4: Write the on-box smoke script**

Create `DockerCompose/lemonade/smoke.sh` (LF line endings):

```sh
#!/bin/sh
# On-box smoke test for mcp-lemonade (run from DockerCompose/): sh lemonade/smoke.sh [host:port]
# Scriptable slice of the on-box validation checklist: health, verbose_json quality
# signals, and incremental PCM streaming.
# Requires curl and python3 ON THE HOST (python3 only to synthesize the test WAV — swap in
# any 16 kHz mono 16-bit wav at /tmp/lemonade-smoke.wav if python3 is unavailable).
set -eu
HOST="${1:-localhost:13305}"
# Override for the NPU tier: sh lemonade/smoke.sh localhost:13305 whisper-v3-turbo-FLM
MODEL="${2:-${STT_MODEL:-Whisper-Medium}}"

echo "== health =="
curl -fsS "http://$HOST/api/v1/health" && echo

echo "== transcription: 1 s 440 Hz tone; expect JSON with text + segments carrying avg_logprob/no_speech_prob =="
python3 - <<'EOF'
import math, struct, wave
w = wave.open('/tmp/lemonade-smoke.wav', 'wb')
w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
w.writeframes(b''.join(struct.pack('<h', int(8000 * math.sin(2 * math.pi * 440 * i / 16000)))
                       for i in range(16000)))
w.close()
EOF
curl -fsS -X POST "http://$HOST/v1/audio/transcriptions" \
  -F "file=@/tmp/lemonade-smoke.wav" -F "model=$MODEL" \
  -F "response_format=verbose_json" -F "language=es"
echo

echo "== speech: streamed pcm; ttfb well under total proves incremental streaming =="
curl -fsS -N -o /tmp/lemonade-smoke.pcm \
  -w 'ttfb=%{time_starttransfer}s total=%{time_total}s bytes=%{size_download}\n' \
  -X POST "http://$HOST/v1/audio/speech" \
  -H "Content-Type: application/json" \
  -d '{"model":"kokoro-v1","input":"Hola, esto es una prueba de síntesis de voz en streaming.","voice":"ef_dora","speed":1.0,"response_format":"pcm","stream_format":"audio"}'
echo "pcm is 24 kHz mono s16le: 48000 bytes per second of audio"
```

- [ ] **Step 5: Rewire docker-compose.yml**

1. Replace the three services `wyoming-whisper` (lines 588–627), `piper-voice-fetch` (629–677), and `wyoming-piper` (679–692) with:

```yaml
  mcp-lemonade:
    image: mcp-lemonade:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: mcp-lemonade
    build: ./lemonade
    ports:
      - "13305:13305"
    environment:
      <<: *timezone
      STT_BACKEND: ${STT_BACKEND:-gpu}
    # Vulkan whisper on the Ryzen AI iGPU. Kokoro TTS is CPU-only on Linux, so it never
    # contends with whisper for the iGPU.
    devices:
      - /dev/dri:/dev/dri
      - /dev/kfd:/dev/kfd
    # /dev/dri/renderD* and /dev/kfd are root:render 0660 on Ubuntu/Debian, and --device
    # preserves host ownership. UID 10001 is in no such group, so without this the render
    # node is EACCES, ggml-vulkan enumerates zero devices, and whisper silently decodes on
    # CPU. Set RENDER_GID from the target host: getent group render | cut -d: -f3
    group_add:
      - "${RENDER_GID:-993}"
    # v11 runs as unprivileged UID 10001 with HOME=/opt/lemonade — these are the real cache
    # paths. (Pre-v11 /root/.cache/* mounts are silently dead: the server still runs but
    # writes land in the container layer and every model re-downloads on recreate.)
    volumes:
      - ./volumes/lemonade-hf-cache:/opt/lemonade/.cache/huggingface
      - ./volumes/lemonade-recipe:/opt/lemonade/.cache/lemonade
      - ./volumes/lemonade-llama:/opt/lemonade/llama
    restart: unless-stopped
    networks:
      - jackbot
```

**Before the first `up`**, create those bind-mount dirs owned by the runtime user. Docker creates missing bind sources as root-owned, UID 10001 then cannot write, and the entrypoint's `cat > config.json` fails under `set -eu` → crash loop:

```bash
mkdir -p DockerCompose/volumes/lemonade-{hf-cache,recipe,llama}
sudo chown -R 10001:10001 DockerCompose/volumes/lemonade-*
```

2. In the `mcp-channel-voice` service, `environment:` — add this under `Announce__Escalation__WebhookUrl: ""` so the STT model can be swapped without editing appsettings (Task 8's NPU tier is exactly this override; the default keeps Whisper-Medium):

```yaml
      Stt__OpenAi__Model: ${STT_MODEL:-Whisper-Medium}
```

3. In the same service's `depends_on:` — replace the `wyoming-whisper:`/`wyoming-piper:` entries with:

```yaml
      mcp-lemonade:
        condition: service_started
```

4. Delete the patched-whisper build context:

```bash
git rm -r DockerCompose/wyoming-whisper
```

- [ ] **Step 6: Extend the no-dri override and the VS Code task**

1. In `DockerCompose/docker-compose.override.no-dri.yml`, append (same `!reset` mechanism as plex/mcp-sandbox; forcing the cpu tier keeps voice working on GPU-less dev hosts):

```yaml
  mcp-lemonade:
    devices: !reset []
    environment:
      STT_BACKEND: cpu
```

2. In `.vscode/tasks.json` line 6, replace `wyoming-whisper wyoming-piper` with `mcp-lemonade` in the `SERVICES` string.

- [ ] **Step 7: Verify (config validation + image build + entrypoint mapping)**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -f DockerCompose/docker-compose.override.no-dri.yml -p jackbot config --quiet
```
Expected: exit 0, no warnings about `wyoming-*` or undefined variables (`STT_BACKEND` defaults apply).

```bash
docker build -t mcp-lemonade:latest DockerCompose/lemonade
```
Expected: image builds.

```bash
docker run --rm -e STT_BACKEND=gpu -e STT_CONFIG_ONLY=1 mcp-lemonade:latest
docker run --rm -e STT_BACKEND=cpu -e STT_CONFIG_ONLY=1 --entrypoint sh mcp-lemonade:latest \
  -c '/entrypoint.sh && cat "$HOME/.cache/lemonade/config.json"'
docker run --rm -e STT_BACKEND=npu -e STT_CONFIG_ONLY=1 mcp-lemonade:latest; echo "exit=$?"
docker run --rm -e STT_BACKEND=bogus -e STT_CONFIG_ONLY=1 mcp-lemonade:latest; echo "exit=$?"
```
Expected, in order: `lemonade: whispercpp.backend=vulkan model=Whisper-Medium` and exit 0; the cpu run prints its banner and then the pretty-printed config the heredoc wrote (`{`, `  "whispercpp": { "backend": "cpu" }`, `}`); the npu run prints the flm-recipe explanation and `exit=1`; the bogus run prints the unknown-backend error and `exit=1`.

Finally — **on the target host only** — prove the GPU is actually reachable, because a `vulkan` banner in the logs does not mean a Vulkan device was found:

```bash
getent group render | cut -d: -f3        # feed this to RENDER_GID
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml \
  -p jackbot up -d mcp-lemonade
docker exec mcp-lemonade vulkaninfo --summary | grep -iE 'deviceName|driverName'
```
Expected: the Radeon 890M listed under a RADV driver. **An empty device list means whisper will decode on CPU while reporting success** — do not proceed past this until it lists the GPU. If Mesa in ubuntu:24.04 turns out not to support gfx1150 well, switch the `gpu` tier to ROCm instead: set `WHISPER_BACKEND="rocm"` for the `gpu` case in the entrypoint (Lemonade's whisper.cpp fork publishes dedicated `linux-rocm-gfx1150` assets — gfx1150 *is* Strix Point — and pulls its own ROCm runtime, needing only `/dev/kfd` + `/dev/dri` and no ICD).

- [ ] **Step 8: Commit**

```bash
git add DockerCompose/lemonade DockerCompose/docker-compose.yml DockerCompose/docker-compose.override.no-dri.yml .vscode/tasks.json
git commit -m "feat(voice): mcp-lemonade compose service with STT_BACKEND tier mapping; drop wyoming containers"
```

---
### Task 8: Optional NPU STT tier — FastFlowLM inside Lemonade (experimental)

**Lemonade drives FastFlowLM itself** — this is the integrated path documented at <https://lemonade-server.ai/flm_npu_linux.html>, not a separate server. `flm` is a Lemonade *recipe* like `llamacpp` or `whispercpp`: Lemonade auto-installs the portable FLM binary if it is not on `PATH`, runs `flm validate` against the NPU stack before loading, and spawns it as a backend subprocess (backends "must run as subprocesses" per Lemonade's own invariants). The FLM backend can hold one ASR + one LLM + one embedding model on the NPU concurrently.

So the tier is **one `mcp-lemonade` container, same base URL, different model** — the hub only changes which model name it asks for. No second service, no second port, no code.

Note this is a *different knob* from `STT_BACKEND`: that variable sets `whispercpp.backend` (the device for the whisper.cpp recipe, whose NPU option is Windows-only). The NPU tier instead selects a model whose recipe is `flm`. The two never mix.

**Opt-in and independently skippable.** Ship `gpu` first; attempt this only once voice works end to end.

**Files:**
- Create: `DockerCompose/docker-compose.override.npu.yml`
- Modify: `CLAUDE.md` (one sentence in the voice paragraph; fold into Task 9's edit if doing both together)

**Interfaces:**
- Consumes: `OpenAiSttConfig.Model` (Task 3) via the `STT_MODEL` compose override added in Task 7. No new code, no new hub setting.
- Produces: `DockerCompose/docker-compose.override.npu.yml`, applied as an extra `-f` after the OS override.

- [ ] **Step 1: Confirm the host stack (do this before writing anything)**

The NPU needs XDNA2 — Strix / Strix Halo / Kraken. The target Ryzen AI 9 HX 370 is Strix Point, so it qualifies; Ryzen AI 7000/8000/200-series are XDNA1 and are **not** supported. On the host:

```bash
ls -l /dev/accel/accel0
cat /sys/class/accel/accel0/device/fw_version    # need >= 1.1.0.0
uname -r                                          # kernel 7.0+, or amdxdna-dkms backport
```

If the device node is missing, install the driver stack from the same PPA Lemonade publishes and reboot:

```bash
sudo add-apt-repository ppa:lemonade-team/stable
sudo apt update
sudo apt install libxrt-npu2 amdxdna-dkms
sudo reboot
```

Stop here if `/dev/accel/accel0` still does not appear — the rest of this task cannot work, and the `gpu` tier remains fully functional.

- [ ] **Step 2: Write the NPU override file**

Follows the repo's existing `docker-compose.override.no-dri.yml` idiom: an opt-in extra `-f` rather than a device list in the base file, so hosts without `/dev/accel/accel0` are never asked to validate it.

Create `DockerCompose/docker-compose.override.npu.yml`:

```yaml
# Opt-in: run Whisper on the Ryzen AI NPU via Lemonade's FastFlowLM (`flm`) recipe.
# EXPERIMENTAL — see docs/superpowers/plans/2026-07-18-voice-lemonade-migration.md Task 8.
#
# Requires an XDNA2 NPU, host amdxdna driver, NPU firmware >= 1.1.0.0. Apply as an extra
# -f AFTER the base and OS override files:
#
#   docker compose \
#     -f DockerCompose/docker-compose.yml \
#     -f DockerCompose/docker-compose.override.linux.yml \
#     -f DockerCompose/docker-compose.override.npu.yml \
#     -p jackbot up -d mcp-lemonade mcp-channel-voice
#
# Lemonade auto-installs the FLM binary into the mounted recipe cache on first load and
# runs `flm validate` before serving. STT_BACKEND is untouched: it still selects the
# whisper.cpp device for the non-NPU tiers, which this override does not use.

services:
  mcp-lemonade:
    devices:
      - /dev/dri:/dev/dri
      - /dev/kfd:/dev/kfd
      - /dev/accel/accel0:/dev/accel/accel0
    # FLM pins NPU buffers. Low memlock is the documented common cause of
    # "mmap ... Resource temporarily unavailable" on whisper load (Lemonade's own FLM-on-Linux
    # troubleshooting sets LimitMEMLOCK=infinity in its systemd unit); issue #1472 reports that
    # failure on Strix Halo but was closed without a diagnosis, so this is a likely fix, not a
    # proven one.
    ulimits:
      memlock: -1
    volumes:
      # FLM keeps its models under $HOME/.flm/models, which none of the base mounts cover —
      # without this the ~0.6 GB NPU whisper re-downloads on every recreate.
      - ./volumes/lemonade-flm:/opt/lemonade/.flm
    environment:
      STT_MODEL: ${STT_MODEL:-whisper-v3-turbo-FLM}

  mcp-channel-voice:
    environment:
      Stt__OpenAi__Model: ${STT_MODEL:-whisper-v3-turbo-FLM}
```

Create that mount dir owned by the runtime user too, exactly as in Task 7 Step 5:

```bash
mkdir -p DockerCompose/volumes/lemonade-flm && sudo chown -R 10001:10001 DockerCompose/volumes/lemonade-flm
```

`devices:` is re-listed in full deliberately. Compose **appends** sequences and dedupes devices by target, so listing all three is redundant-but-harmless and keeps the override readable as the complete device set rather than a diff. (The sibling `docker-compose.override.no-dri.yml` needs the `!reset` tag precisely because plain merging cannot *remove* an entry.) `environment:` and `ulimits:` are mappings and merge key-wise as expected.

- [ ] **Step 3: Bootstrap the FLM backend, then confirm the model name**

There is a chicken-and-egg here that will otherwise look like "the NPU tier just doesn't work". Lemonade discovers FLM models by shelling out to the FLM binary, but it only auto-installs that binary inside the FLM backend's `load()` — which you cannot reach, because no FLM model is in the catalog to request. An empty catalog is the exact symptom of open issue #2094. Break the cycle by installing the backend explicitly first:

```bash
curl -fsS -X POST http://localhost:13305/api/v1/install \
  -H "Content-Type: application/json" \
  -d '{"recipe": "flm", "backend": "npu"}'
```

That drops the FLM binary into the recipe cache (persisted by the Task 7 `lemonade-recipe` mount), after which discovery populates. Now confirm the catalog name:

```bash
curl -fsS http://localhost:13305/api/v1/models | grep -i -E 'whisper|flm'
```

Expect **`whisper-v3-turbo-FLM`**. That is the name Lemonade derives from FLM's own `whisper-v3:turbo` — it rewrites `:` to `-` and appends `-FLM` — so the colon form is the *checkpoint*, not a model name, and passing it as `model` will not resolve. FLM v0.9.45 ships exactly one ASR model, so there should be exactly one match.

If discovery still comes up empty, register it explicitly as a user model (this is the one place the colon form is correct, and the `user.` prefix is required when supplying an explicit checkpoint and recipe):

```bash
curl -fsS -X POST http://localhost:13305/api/v1/pull \
  -H "Content-Type: application/json" \
  -d '{"model_name": "user.Whisper-NPU", "checkpoint": "whisper-v3:turbo", "recipe": "flm"}'
```

then set `STT_MODEL=user.Whisper-NPU`. Put whichever name actually resolved into the override file.

- [ ] **Step 4: Bring it up and verify on-box**

```bash
docker compose \
  -f DockerCompose/docker-compose.yml \
  -f DockerCompose/docker-compose.override.linux.yml \
  -f DockerCompose/docker-compose.override.npu.yml \
  -p jackbot up -d mcp-lemonade mcp-channel-voice

docker logs -f mcp-lemonade   # expect flm validate + FLM backend load, not a whisper.cpp banner

curl -fsS -X POST http://localhost:13305/v1/audio/transcriptions \
  -F "file=@/tmp/lemonade-smoke.wav" -F "model=whisper-v3-turbo-FLM" -F "response_format=verbose_json"
```

Expected: JSON carrying `model` and `text` — **and nothing else** (see below). Two failure modes to recognize rather than debug:
- `mmap … Resource temporarily unavailable` → memlock is not taking effect in the container; if raising it does not help, this is [issue #1472](https://github.com/lemonade-sdk/lemonade/issues/1472), closed unresolved. Fall back to `gpu`.
- Transcription succeeds but the logs show whisper.cpp → the FLM recipe was not selected; the model name is wrong (back to Step 3).

Then speak to a satellite and confirm a transcript arrives end to end.

**Two functional regressions on this tier, both confirmed in FLM v0.9.45's source — not predictions:**

1. **The quality gate goes silent.** FLM's transcription handler returns exactly `{"model", "text"}`: no segments, no `avg_logprob`, no `no_speech_prob`. `TranscriptDispatcher` therefore fails open and dispatches everything, including the gibberish the `gpu` tier drops. `response_format` is ignored, so `verbose_json` changes nothing.
2. **Spanish forcing is lost.** FLM never reads the `language` field, so `Stt__OpenAi__Language=es` is inert here and transcription relies on whisper-v3-turbo's auto-detect.

Together these are a strong argument for keeping `gpu` as the daily driver even if NPU latency or power draw looks better. Weigh them explicitly before adopting.

- [ ] **Step 5: Commit**

```bash
git add DockerCompose/docker-compose.override.npu.yml
git commit -m "feat(voice): optional NPU STT tier via Lemonade's FastFlowLM recipe"
```

---

### Task 9: Documentation + final verification

**Files:**
- Modify: `CLAUDE.md` (Projects table row for `McpChannelVoice`; both `docker compose … up` commands at lines 120 and 123; the "Voice Satellite Architecture" paragraph at line 183)

**Interfaces:** none (docs + verification only).

- [ ] **Step 1: Update CLAUDE.md**

1. Projects-table row for `McpChannelVoice` — replace `whisper STT, piper TTS` with `Lemonade STT/TTS (OpenAI-compatible)` so the row reads:

```markdown
| `McpChannelVoice` | Voice channel — Wyoming hub that dials hardware satellites, Lemonade STT/TTS (OpenAI-compatible), follow-up windows, announcements; dual-role: exposes `filesystem://timers` (hub-local countdown timers that ring insistently) |
```

2. In both launch commands (lines 120 and 123), replace `wyoming-whisper wyoming-piper` with `mcp-lemonade`.

3. In the "Voice Satellite Architecture" paragraph, replace the pipeline sentence fragment `→ wyoming-whisper STT → transcript dispatched as \`channel/message\` → agent reply synthesized by wyoming-piper → streamed back` with:

```markdown
→ Lemonade STT (`mcp-lemonade`, OpenAI `/v1/audio/transcriptions`, Whisper-Medium on whisper.cpp; device via `STT_BACKEND` ∈ cpu|gpu — or optionally the experimental NPU tier through Lemonade's `flm` recipe, enabled by `docker-compose.override.npu.yml` + `STT_MODEL`) → transcript dispatched as `channel/message` → agent reply synthesized by Lemonade Kokoro (`/v1/audio/speech`, streamed 24 kHz PCM resampled in-hub to 22 050 Hz) → streamed back
```

- [ ] **Step 2: Full verification sweep**

```bash
dotnet build agent.sln
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"
grep -rn "wyoming-whisper\|wyoming-piper\|piper-voice-fetch" --include="*.cs" --include="*.json" --include="*.yml" --include="*.md" . | grep -v "obj/\|bin/\|satellite/\|docs/superpowers/\|volumes/"
```

Expected: build green; unit suite green (judge any stray failure by type against the known Docker-baseline flakes, not by count); the grep returns **zero hits** — every reference to the dead containers is gone from code, config, compose, and CLAUDE.md (the design spec and this plan under `docs/superpowers/` keep theirs as history).

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs(voice): document Lemonade STT/TTS cutover in CLAUDE.md"
```

---

## On-box validation checklist (target host: Ryzen AI 9 HX 370, after deploy)

Copied from the spec — these are empirical checks the design does **not** assume; run them on the mini-PC with the stack up (`sh DockerCompose/lemonade/smoke.sh` covers the first and third):

**The GPU checks are not optional.** Three independent conditions — no Vulkan ICD in the image, wrong-owner bind mounts, and render-node group permissions — each produce a container that starts cleanly, logs `Using backend: vulkan`, and decodes on CPU. Tasks 7 and 8 fix all three, but only the box can prove it.

- [ ] **`docker exec lemonade vulkaninfo --summary` lists the Radeon 890M** under a RADV driver. The backend banner in `docker logs` is NOT evidence of GPU use. Then time a transcription: expect ~0.08–0.1 RTF, not ~0.3–0.5.
- [ ] `getent group render | cut -d: -f3` on the host, and `RENDER_GID` set to that value wherever compose reads it. (If `/dev/dri/renderD*` happens to be mode 0666 this is a no-op — check, don't assume.)
- [ ] Mesa in ubuntu:24.04 genuinely supports gfx1150 (RDNA 3.5). If Vulkan underperforms or misbehaves, switch the `gpu` tier to `rocm` — the fork ships `linux-rocm-gfx1150` assets and needs no ICD.
- [ ] Bind-mount persistence: `DockerCompose/volumes/lemonade-*` owned by UID 10001, and models survive `docker compose up --force-recreate` (a re-download means a dead mount).
- [ ] Streaming: `response_format=pcm` + `stream_format=audio` stream incrementally (smoke.sh `ttfb` ≪ `total`). Source says `speed` does not disable streaming — confirm if you change it from 1.0.
- [ ] Resampled TTS sounds correct through a real satellite (no pitch shift, no boundary clicks) — say something and listen to the reply on `fran-office-01`.
- [ ] `ef_dora` exists in the voice pack the `kokoro-v1` checkpoint downloads (near-certain, but one request confirms).
- [ ] `verbose_json` really returns `avg_logprob` + `no_speech_prob` on the deployed binary. Source-confirmed in the pinned whisper.cpp-rocm v1.8.4 server, so this is supply-chain sanity rather than a design question; `compression_ratio` is deliberately unimplemented upstream, matching the client leaving it null.
- [ ] **Scrub removed Wyoming keys from the deployed hub's environment** (the pi5 stack's compose `.env` / `SATELLITES__*`-style vars): `Stt__Wyoming__*`, `Tts__Wyoming__*`, and `Stt__*__ConfidenceThreshold` no longer exist and bind to nothing with **no warning** — a leftover threshold override silently stops applying instead of failing loudly. Grep the deployed env for `Wyoming` and `ConfidenceThreshold`; the replacements are `Stt__OpenAi__AvgLogProbThreshold` / `Stt__OpenAi__NoSpeechProbThreshold`.
- [ ] NPU tier (Task 8, only if attempted): XDNA2 part, `/dev/accel/accel0` present with a usable group, firmware ≥ 1.1.0.0, memlock unlimited; `POST /api/v1/install` bootstraps FLM, `whisper-v3-turbo-FLM` appears in the catalog, and the logs show FLM rather than whisper.cpp. Compare accuracy **and** latency against `gpu` before adopting, remembering that on this tier the quality gate is inert and `language=es` is ignored.

## Deviations from the spec (deliberate)

- **The NPU tier is a model choice, not a `whispercpp.backend` value.** This resolves the spec's open question ("whether `whispercpp.backend=npu` routes to FLM… had conflicting evidence"): it does not — Lemonade's whisper.cpp NPU backend is **Windows-only**. On Linux, FastFlowLM is reached through Lemonade's separate **`flm` recipe**, which Lemonade auto-installs, validates (`flm validate`) and supervises as a backend subprocess. Same container, same base URL, different model name. The spec's always-mounted `/dev/accel/accel0` becomes an opt-in `docker-compose.override.npu.yml` (matching the repo's existing `no-dri` override idiom) so hosts without an NPU never validate the device. Known risk carried forward: lemonade issue #1472 reports `mmap … Resource temporarily unavailable` loading whisper under FLM, hence "experimental" and gpu-first.
- **`OpenAiTtsConfig` carries a `TrailingSilenceTrimThreshold` the spec's field list omits** (spec: `BaseUrl`, `Model`, `Voice`, `Speed`). It moves over from `WyomingTtsConfig` because the spec also keeps `SilenceTrimmingTextToSpeech`, and that wrapper needs a threshold to read; deleting the Wyoming config without rehoming the setting would silently disable trailing-silence trimming and widen the gap before the follow-up chime.
- **The FLM ASR model is `whisper-v3-turbo-FLM`.** Lemonade derives catalog names from FLM's own list by rewriting `:` to `-` and appending `-FLM`, so FLM's `whisper-v3:turbo` is the *checkpoint* and does not resolve as a `model`. Because FLM discovery returns nothing until the FLM binary exists — and Lemonade only auto-installs it inside the backend's `load()`, which needs a catalog entry to reach (issue #2094's deadlock) — Task 8 Step 3 bootstraps with `POST /api/v1/install {"recipe":"flm","backend":"npu"}` before listing models.
- **The NPU tier drops two behaviors, both confirmed in FLM v0.9.45's source rather than merely suspected.** Its transcription handler returns only `{"model","text"}`: no segment stats, so the gibberish gate is inert; and it never reads `language`, so Spanish forcing falls back to auto-detect. Documented in Task 8 Step 4 as facts, not risks.
- **`STT_BACKEND` selects only Lemonade's whisper.cpp device; the NPU tier is selected by `STT_MODEL`.** The spec wanted one variable expanding to `{backend, model, mounts}`, but `cpu`/`gpu` share a model and the NPU tier is a different *recipe*, so the two concerns split cleanly. The hub keeps the spec's own `OpenAiSttConfig` field list (`BaseUrl`, `Model`, `Language`, thresholds) with no `Backend` field.
- **The NPU tier loses the gibberish gate.** FLM does not document `verbose_json` segment stats, so `AvgLogProb`/`NoSpeechProb` will likely arrive null and `TranscriptDispatcher` fails open. Acceptable (that is the designed degradation) but it is a real functional difference from the `gpu` tier, not just a performance one.
- **Backend selection writes `config.json` directly** instead of running `lemonade config set`: the `lemonade` CLI is a client of a *running* server, so the entrypoint cannot use it before startup; `config.json` is the documented mechanism Lemonade's own docker guide uses (for `llamacpp`).




