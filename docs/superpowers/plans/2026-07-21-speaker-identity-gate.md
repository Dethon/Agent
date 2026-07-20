# Speaker-Identity Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reject voice captures whose speaker is not an enrolled household member, before STT, so background TV speech never reaches whisper or the agent.

**Architecture:** A new `McpChannelVoice/Services/Verification/` unit: C# Kaldi-compatible fbank frontend → ONNX speaker-embedding model (CAM++ via `Microsoft.ML.OnnxRuntime`) → cosine similarity against profiles built from `voices/<identity>/*.wav`. `WyomingSatelliteHost.TranscribeAndDispatchAsync` consults the verifier on the capture's speech-classified audio and returns `false` (existing end-conversation path) on rejection. Fail-open everywhere.

**Tech Stack:** .NET 10, Microsoft.ML.OnnxRuntime, xUnit + Shouldly + Moq, Docker (model baked via `ADD --checksum`), bash enrollment script.

**Spec:** `docs/superpowers/specs/2026-07-21-speaker-identity-gate-design.md` — read it first.

## Global Constraints

- `.cs` files have **no trailing newline** (`.editorconfig` `insert_final_newline = false`).
- TDD: write the failing test, watch it fail, then implement. Run `dotnet test Tests/Tests.csproj --filter "<given filter>"`.
- The pre-commit hook re-stages **whole files** after `dotnet format` — make the working tree match the commit.
- Repo style: file-scoped namespaces, primary constructors, records for DTOs, no XML doc comments, LINQ over loops **except** DSP hot paths (fbank/FFT are explicitly hot paths — plain loops there).
- Packages are plain `PackageReference` per csproj (no central management).
- Commit after each task with the message given in the task.
- All new production code lives in `McpChannelVoice` (namespace `McpChannelVoice.Services.Verification`) except the two Domain metric additions in Task 6.

---

### Task 1: Fbank frontend (FFT + FbankExtractor + golden vectors)

**Files:**
- Create: `McpChannelVoice/Services/Verification/Fft.cs`
- Create: `McpChannelVoice/Services/Verification/FbankExtractor.cs`
- Create: `Tests/Unit/McpChannelVoice/Verification/FbankExtractorTests.cs`
- Create: `Tests/Unit/McpChannelVoice/Verification/Fixtures/fbank-golden.json` (generated in Step 1)
- Modify: `Tests/Tests.csproj` (copy fixtures to output)

**Interfaces:**
- Consumes: nothing (leaf unit).
- Produces: `FbankExtractor.Extract(ReadOnlySpan<byte> pcmS16Le) -> float[][]` (frames × 80, Kaldi log-mel, int16-scale input, no normalization) and `static FbankExtractor.MeanNormalize(float[][] frames)` (in-place per-dim CMN). `Fft.Transform(float[] re, float[] im)` in-place.

- [ ] **Step 1: Generate the golden fixture with the reference Kaldi implementation**

```bash
mkdir -p Tests/Unit/McpChannelVoice/Verification/Fixtures
cat > /tmp/gen_golden.py <<'EOF'
import json
import numpy as np
import kaldi_native_fbank as knf

sr = 16000
n = 8000  # 0.5 s
i = np.arange(n)
sig = np.round((0.3 * np.sin(2 * np.pi * 440 * i / sr)
                + 0.2 * np.sin(2 * np.pi * 1337 * i / sr + 1.0)) * 32767)
sig = sig.astype(np.int16).astype(np.float32)  # int16-quantized, Kaldi int16 scale

opts = knf.FbankOptions()
opts.frame_opts.samp_freq = sr
opts.frame_opts.dither = 0.0
opts.mel_opts.num_bins = 80

fb = knf.OnlineFbank(opts)
fb.accept_waveform(sr, sig.tolist())
fb.input_finished()
frames = [fb.get_frame(k).tolist() for k in range(fb.num_frames_ready)]
json.dump({"frames": frames}, open("/out/fbank-golden.json", "w"))
print(f"{len(frames)} frames x {len(frames[0])} bins")
EOF
docker run --rm -v "$PWD/Tests/Unit/McpChannelVoice/Verification/Fixtures:/out" -v /tmp/gen_golden.py:/gen_golden.py \
  python:3.12-slim bash -c "pip install -q numpy kaldi-native-fbank && python /gen_golden.py"
```

Expected output: `48 frames x 80 bins` (snip_edges framing: (8000−400)/160+1 = 48). If the frame count differs, stop and investigate before continuing.

- [ ] **Step 2: Make the fixture available to tests**

In `Tests/Tests.csproj`, check whether an `ItemGroup` already copies fixture files (`grep -n "CopyToOutputDirectory" Tests/Tests.csproj`). Add (or extend an existing group with):

```xml
  <ItemGroup>
    <None Include="Unit/McpChannelVoice/Verification/Fixtures/**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Write the failing tests**

`Tests/Unit/McpChannelVoice/Verification/FbankExtractorTests.cs`:

```csharp
using System.Text.Json;
using McpChannelVoice.Services.Verification;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class FbankExtractorTests
{
    private static byte[] GoldenSignalPcm()
    {
        const int sr = 16_000;
        var pcm = new byte[8000 * 2];
        for (var i = 0; i < 8000; i++)
        {
            var s = 0.3 * Math.Sin(2 * Math.PI * 440 * i / sr)
                    + 0.2 * Math.Sin(2 * Math.PI * 1337 * i / sr + 1.0);
            var v = (short)Math.Round(s * 32767);
            pcm[2 * i] = (byte)(v & 0xFF);
            pcm[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return pcm;
    }

    [Fact]
    public void Extract_GoldenSignal_MatchesKaldiReference()
    {
        var goldenPath = Path.Combine(
            AppContext.BaseDirectory, "Unit", "McpChannelVoice", "Verification", "Fixtures", "fbank-golden.json");
        var golden = JsonSerializer.Deserialize<GoldenFile>(
            File.ReadAllText(goldenPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var frames = new FbankExtractor().Extract(GoldenSignalPcm());

        frames.Length.ShouldBe(golden.Frames.Count);
        for (var f = 0; f < frames.Length; f++)
        {
            frames[f].Length.ShouldBe(80);
            for (var b = 0; b < 80; b++)
            {
                Math.Abs(frames[f][b] - golden.Frames[f][b]).ShouldBeLessThan(0.02f,
                    $"frame {f} bin {b}: got {frames[f][b]}, golden {golden.Frames[f][b]}");
            }
        }
    }

    [Fact]
    public void Extract_ShorterThanOneFrame_ReturnsNoFrames()
    {
        new FbankExtractor().Extract(new byte[300 * 2]).ShouldBeEmpty();
    }

    [Fact]
    public void MeanNormalize_ZeroesThePerDimensionMean()
    {
        var frames = new FbankExtractor().Extract(GoldenSignalPcm());

        FbankExtractor.MeanNormalize(frames);

        for (var b = 0; b < 80; b++)
        {
            var mean = 0.0;
            for (var f = 0; f < frames.Length; f++)
            { mean += frames[f][b]; }
            Math.Abs(mean / frames.Length).ShouldBeLessThan(1e-4);
        }
    }

    private sealed record GoldenFile(List<float[]> Frames);
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FbankExtractorTests" 2>&1 | tail -5`
Expected: build error `The type or namespace name 'FbankExtractor' could not be found` (compile failure is the red for a missing type).

- [ ] **Step 5: Implement Fft**

`McpChannelVoice/Services/Verification/Fft.cs`:

```csharp
namespace McpChannelVoice.Services.Verification;

// In-place iterative radix-2 FFT. DSP hot path: plain loops by design.
internal static class Fft
{
    public static void Transform(float[] re, float[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            { j ^= bit; }
            j |= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wRe = (float)Math.Cos(ang);
            var wIm = (float)Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                float curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm;
                    var vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe;
                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + len / 2] = uRe - vRe;
                    im[i + k + len / 2] = uIm - vIm;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
```

- [ ] **Step 6: Implement FbankExtractor**

`McpChannelVoice/Services/Verification/FbankExtractor.cs`:

```csharp
namespace McpChannelVoice.Services.Verification;

// Kaldi-compatible 80-dim log-mel filterbank (25 ms window / 10 ms shift, 16 kHz,
// dither 0, snip-edges, Povey window, int16-scale input). Matches what the
// WeSpeaker/CAM++ ONNX speaker models were trained on; verified against
// kaldi-native-fbank golden vectors. Input scale conventions cancel under the
// per-utterance mean subtraction (a global gain is a constant additive offset in
// log-mel space), but the golden vectors pin the raw int16-scale convention anyway.
// DSP hot path: plain loops by design.
public sealed class FbankExtractor
{
    private const int SampleRate = 16_000;
    private const int FrameLength = 400;
    private const int FrameShift = 160;
    private const int FftSize = 512;
    private const int NumBins = 80;
    private const float PreEmphasis = 0.97f;
    private const double LowFreqHz = 20;
    private const double HighFreqHz = SampleRate / 2.0;

    private readonly float[] _window = BuildPoveyWindow();
    private readonly (int Offset, float[] Weights)[] _mel = BuildMelBanks();

    public float[][] Extract(ReadOnlySpan<byte> pcmS16Le)
    {
        var sampleCount = pcmS16Le.Length / 2;
        if (sampleCount < FrameLength)
        {
            return [];
        }

        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(pcmS16Le[2 * i] | (pcmS16Le[2 * i + 1] << 8));
        }

        var frameCount = (sampleCount - FrameLength) / FrameShift + 1;
        var frames = new float[frameCount][];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var power = new float[FftSize / 2 + 1];

        for (var f = 0; f < frameCount; f++)
        {
            Array.Clear(re);
            Array.Clear(im);
            var start = f * FrameShift;

            var mean = 0f;
            for (var i = 0; i < FrameLength; i++)
            { mean += samples[start + i]; }
            mean /= FrameLength;

            for (var i = 0; i < FrameLength; i++)
            { re[i] = samples[start + i] - mean; }

            for (var i = FrameLength - 1; i > 0; i--)
            { re[i] -= PreEmphasis * re[i - 1]; }
            re[0] -= PreEmphasis * re[0];

            for (var i = 0; i < FrameLength; i++)
            { re[i] *= _window[i]; }

            Fft.Transform(re, im);
            for (var k = 0; k <= FftSize / 2; k++)
            { power[k] = re[k] * re[k] + im[k] * im[k]; }

            var bins = new float[NumBins];
            for (var b = 0; b < NumBins; b++)
            {
                var (offset, weights) = _mel[b];
                var energy = 0f;
                for (var k = 0; k < weights.Length; k++)
                { energy += weights[k] * power[offset + k]; }
                bins[b] = MathF.Log(MathF.Max(energy, float.Epsilon));
            }
            frames[f] = bins;
        }
        return frames;
    }

    public static void MeanNormalize(float[][] frames)
    {
        if (frames.Length == 0)
        {
            return;
        }
        for (var b = 0; b < NumBins; b++)
        {
            var mean = 0f;
            for (var f = 0; f < frames.Length; f++)
            { mean += frames[f][b]; }
            mean /= frames.Length;
            for (var f = 0; f < frames.Length; f++)
            { frames[f][b] -= mean; }
        }
    }

    private static float[] BuildPoveyWindow()
    {
        var window = new float[FrameLength];
        for (var i = 0; i < FrameLength; i++)
        {
            var hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameLength - 1));
            window[i] = (float)Math.Pow(hann, 0.85);
        }
        return window;
    }

    private static double MelScale(double hz) => 1127.0 * Math.Log(1.0 + hz / 700.0);

    private static (int Offset, float[] Weights)[] BuildMelBanks()
    {
        var melLow = MelScale(LowFreqHz);
        var melHigh = MelScale(HighFreqHz);
        var banks = new (int, float[])[NumBins];
        var binHz = (double)SampleRate / FftSize;

        for (var b = 0; b < NumBins; b++)
        {
            var left = melLow + b * (melHigh - melLow) / (NumBins + 1);
            var center = melLow + (b + 1) * (melHigh - melLow) / (NumBins + 1);
            var right = melLow + (b + 2) * (melHigh - melLow) / (NumBins + 1);

            var weights = new List<float>();
            var offset = -1;
            for (var k = 0; k <= FftSize / 2; k++)
            {
                var mel = MelScale(k * binHz);
                var weight = 0.0;
                if (mel > left && mel < right)
                {
                    weight = mel <= center
                        ? (mel - left) / (center - left)
                        : (right - mel) / (right - center);
                }
                if (weight > 0)
                {
                    if (offset < 0)
                    { offset = k; }
                    weights.Add((float)weight);
                }
                else if (offset >= 0)
                {
                    break;
                }
            }
            banks[b] = (Math.Max(offset, 0), weights.ToArray());
        }
        return banks;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~FbankExtractorTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 3`.

If `Extract_GoldenSignal_MatchesKaldiReference` fails with small systematic offsets, the usual suspects (in order): Povey window exponent (0.85), pre-emphasis edge handling (`re[0] -= c*re[0]`), DC removal before pre-emphasis, mel low/high frequencies (20 Hz / 8000 Hz). Fix the frontend — never widen the tolerance past 0.02.

- [ ] **Step 8: Commit**

```bash
git add McpChannelVoice/Services/Verification/Fft.cs McpChannelVoice/Services/Verification/FbankExtractor.cs Tests/Unit/McpChannelVoice/Verification/ Tests/Tests.csproj
git commit -m "feat(voice): Kaldi-compatible fbank frontend for speaker verification"
```

---

### Task 2: ONNX speaker embedder

**Files:**
- Modify: `McpChannelVoice/McpChannelVoice.csproj` (add package)
- Create: `McpChannelVoice/Services/Verification/ISpeakerEmbedder.cs`
- Create: `McpChannelVoice/Services/Verification/OnnxSpeakerEmbedder.cs`
- Create: `Tests/Unit/McpChannelVoice/Verification/EmbeddingMathTests.cs`

**Interfaces:**
- Consumes: `FbankExtractor` from Task 1.
- Produces: `ISpeakerEmbedder { float[] Embed(ReadOnlySpan<byte> pcmS16Le); }` (returns L2-normalized embedding; throws on failure — callers fail open). Statics `OnnxSpeakerEmbedder.L2Normalize(float[] v) -> float[]` and `OnnxSpeakerEmbedder.Cosine(float[] a, float[] b) -> double` (assumes normalized inputs).

- [ ] **Step 1: Add the package**

In `McpChannelVoice/McpChannelVoice.csproj`, next to the existing `PackageReference` items:

```xml
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.22.0" />
```

Run: `dotnet restore McpChannelVoice/McpChannelVoice.csproj` — must succeed. If 1.22.0 is unavailable, use the newest 1.2x version listed by `dotnet package search Microsoft.ML.OnnxRuntime --take 3`.

- [ ] **Step 2: Write the failing math tests**

`Tests/Unit/McpChannelVoice/Verification/EmbeddingMathTests.cs`:

```csharp
using McpChannelVoice.Services.Verification;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class EmbeddingMathTests
{
    [Fact]
    public void L2Normalize_ScalesToUnitLength()
    {
        var v = OnnxSpeakerEmbedder.L2Normalize([3f, 4f]);
        v[0].ShouldBe(0.6f, 1e-5f);
        v[1].ShouldBe(0.8f, 1e-5f);
    }

    [Fact]
    public void L2Normalize_ZeroVector_ReturnsZeroVector()
    {
        OnnxSpeakerEmbedder.L2Normalize([0f, 0f]).ShouldBe([0f, 0f]);
    }

    [Fact]
    public void Cosine_IdenticalNormalizedVectors_IsOne()
    {
        var v = OnnxSpeakerEmbedder.L2Normalize([1f, 2f, 3f]);
        OnnxSpeakerEmbedder.Cosine(v, v).ShouldBe(1.0, 1e-5);
    }

    [Fact]
    public void Cosine_OrthogonalVectors_IsZero()
    {
        OnnxSpeakerEmbedder.Cosine([1f, 0f], [0f, 1f]).ShouldBe(0.0, 1e-9);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~EmbeddingMathTests" 2>&1 | tail -5`
Expected: compile error, `OnnxSpeakerEmbedder` not found.

- [ ] **Step 4: Implement the embedder**

`McpChannelVoice/Services/Verification/ISpeakerEmbedder.cs`:

```csharp
namespace McpChannelVoice.Services.Verification;

public interface ISpeakerEmbedder
{
    // 16 kHz mono S16LE in, L2-normalized speaker embedding out. Throws on
    // failure (model errors, audio too short) — callers treat that as fail-open.
    float[] Embed(ReadOnlySpan<byte> pcmS16Le);
}
```

`McpChannelVoice/Services/Verification/OnnxSpeakerEmbedder.cs`:

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace McpChannelVoice.Services.Verification;

// Runs a WeSpeaker/CAM++-family speaker-embedding ONNX model: input [1, T, 80]
// mean-normalized fbank features, output [1, D] speaker embedding. InferenceSession
// is thread-safe for concurrent Run calls; one instance serves the whole hub.
public sealed class OnnxSpeakerEmbedder(string modelPath) : ISpeakerEmbedder, IDisposable
{
    private readonly InferenceSession _session = new(modelPath);

    public float[] Embed(ReadOnlySpan<byte> pcmS16Le)
    {
        var frames = new FbankExtractor().Extract(pcmS16Le);
        if (frames.Length == 0)
        {
            throw new InvalidOperationException("Audio too short to embed");
        }
        FbankExtractor.MeanNormalize(frames);

        var tensor = new DenseTensor<float>([1, frames.Length, 80]);
        for (var f = 0; f < frames.Length; f++)
        {
            for (var b = 0; b < 80; b++)
            { tensor[0, f, b] = frames[f][b]; }
        }

        var inputName = _session.InputMetadata.Keys.First();
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        return L2Normalize(results.First().AsEnumerable<float>().ToArray());
    }

    public static float[] L2Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }

    public static double Cosine(float[] a, float[] b) =>
        a.Zip(b, (x, y) => (double)x * y).Sum();

    public void Dispose() => _session.Dispose();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~EmbeddingMathTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 4`. Also run `dotnet build agent.sln 2>&1 | grep -E "error|Build succeeded" | head -3` — must succeed.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/McpChannelVoice.csproj McpChannelVoice/Services/Verification/ISpeakerEmbedder.cs McpChannelVoice/Services/Verification/OnnxSpeakerEmbedder.cs Tests/Unit/McpChannelVoice/Verification/EmbeddingMathTests.cs
git commit -m "feat(voice): ONNX speaker embedder over the fbank frontend"
```

---

### Task 3: Speaker profile store (enrollment WAVs → cached profiles)

**Files:**
- Create: `McpChannelVoice/Services/Verification/SpeakerProfile.cs`
- Create: `McpChannelVoice/Services/Verification/SpeakerProfileStore.cs`
- Create: `Tests/Unit/McpChannelVoice/Verification/SpeakerProfileStoreTests.cs`

**Interfaces:**
- Consumes: `ISpeakerEmbedder` (Task 2), `OnnxSpeakerEmbedder.L2Normalize`.
- Produces: `sealed record SpeakerProfile(string Name, float[] Embedding)`; `SpeakerProfileStore(string voicesPath, ISpeakerEmbedder embedder, ILogger<SpeakerProfileStore> logger)` with `IReadOnlyList<SpeakerProfile> Load()`.

- [ ] **Step 1: Write the failing tests**

`Tests/Unit/McpChannelVoice/Verification/SpeakerProfileStoreTests.cs`:

```csharp
using McpChannelVoice.Services.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class SpeakerProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"voices-{Guid.NewGuid()}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        { Directory.Delete(_dir, true); }
    }

    // Embeds every WAV to a vector derived from its first sample so tests can
    // predict profile math without a real model.
    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Calls;
        public float[] Embed(ReadOnlySpan<byte> pcmS16Le)
        {
            Calls++;
            var first = (short)(pcmS16Le[0] | (pcmS16Le[1] << 8));
            return OnnxSpeakerEmbedder.L2Normalize([first, 1f]);
        }
    }

    // Minimal valid 16 kHz mono S16LE RIFF file whose samples all carry `value`.
    private static byte[] Wav(short value, int samples = 1600)
    {
        var data = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            data[2 * i] = (byte)(value & 0xFF);
            data[2 * i + 1] = (byte)((value >> 8) & 0xFF);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(16_000);        // sample rate
        w.Write(16_000 * 2);    // byte rate
        w.Write((short)2);      // block align
        w.Write((short)16);     // bits
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    private void WriteVoice(string name, params byte[][] wavs)
    {
        var d = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        for (var i = 0; i < wavs.Length; i++)
        { File.WriteAllBytes(Path.Combine(d, $"sample-{i}.wav"), wavs[i]); }
    }

    [Fact]
    public void Load_TwoIdentities_BuildsNormalizedMeanProfiles()
    {
        WriteVoice("fran", Wav(1000), Wav(2000));
        WriteVoice("ana", Wav(-500));
        var store = new SpeakerProfileStore(_dir, new FakeEmbedder(), NullLogger<SpeakerProfileStore>.Instance);

        var profiles = store.Load();

        profiles.Count.ShouldBe(2);
        var fran = profiles.Single(p => p.Name == "fran");
        // mean of normalized [1000,1] and [2000,1], re-normalized => unit length
        Math.Sqrt(fran.Embedding.Sum(x => (double)x * x)).ShouldBe(1.0, 1e-5);
        var ana = profiles.Single(p => p.Name == "ana");
        ana.Embedding[0].ShouldBeLessThan(0); // sign of the -500 sample survives
    }

    [Fact]
    public void Load_SecondCall_UsesCacheInsteadOfReEmbedding()
    {
        WriteVoice("fran", Wav(1000), Wav(2000));
        var embedder = new FakeEmbedder();
        var store = new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance);

        var first = store.Load();
        var again = new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance).Load();

        embedder.Calls.ShouldBe(2); // only the first Load embedded
        again.Single().Embedding.ShouldBe(first.Single().Embedding);
    }

    [Fact]
    public void Load_WavChanged_InvalidatesCache()
    {
        WriteVoice("fran", Wav(1000));
        var embedder = new FakeEmbedder();
        new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance).Load();

        File.WriteAllBytes(Path.Combine(_dir, "fran", "sample-0.wav"), Wav(3000, samples: 3200));
        new SpeakerProfileStore(_dir, embedder, NullLogger<SpeakerProfileStore>.Instance).Load();

        embedder.Calls.ShouldBe(2);
    }

    [Fact]
    public void Load_WrongFormatWav_IsSkippedWithoutFailing()
    {
        var stereo = Wav(1000);
        stereo[22] = 2; // channels = 2
        WriteVoice("fran", stereo, Wav(2000));

        var profiles = new SpeakerProfileStore(_dir, new FakeEmbedder(), NullLogger<SpeakerProfileStore>.Instance).Load();

        profiles.Single().Name.ShouldBe("fran"); // built from the one valid file
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsEmpty()
    {
        new SpeakerProfileStore(Path.Combine(_dir, "nope"), new FakeEmbedder(), NullLogger<SpeakerProfileStore>.Instance)
            .Load().ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpeakerProfileStoreTests" 2>&1 | tail -5`
Expected: compile error, `SpeakerProfileStore` not found.

- [ ] **Step 3: Implement**

`McpChannelVoice/Services/Verification/SpeakerProfile.cs`:

```csharp
namespace McpChannelVoice.Services.Verification;

public sealed record SpeakerProfile(string Name, float[] Embedding);
```

`McpChannelVoice/Services/Verification/SpeakerProfileStore.cs`:

```csharp
using System.Text.Json;

namespace McpChannelVoice.Services.Verification;

// Builds one profile per voices/<identity>/ directory: each 16 kHz mono S16LE WAV is
// embedded, embeddings are averaged and re-normalized. Embeddings are cached in
// profile.json beside the WAVs, keyed by file name/length/mtime, so startup does not
// re-run the model when nothing changed. Wrong-format WAVs are skipped with a warning.
public sealed class SpeakerProfileStore(string voicesPath, ISpeakerEmbedder embedder, ILogger<SpeakerProfileStore> logger)
{
    private sealed record CacheEntry(List<CachedFile> Files, float[] Embedding);
    private sealed record CachedFile(string Name, long Length, DateTime ModifiedUtc);

    public IReadOnlyList<SpeakerProfile> Load()
    {
        if (!Directory.Exists(voicesPath))
        {
            logger.LogInformation("Voices path {Path} does not exist; no speaker profiles", voicesPath);
            return [];
        }

        return Directory.EnumerateDirectories(voicesPath)
            .Select(dir => BuildProfile(dir))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
    }

    private SpeakerProfile? BuildProfile(string dir)
    {
        var name = Path.GetFileName(dir);
        var files = Directory.EnumerateFiles(dir, "*.wav")
            .OrderBy(f => f)
            .Select(f => new FileInfo(f))
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var manifest = files
            .Select(f => new CachedFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
        var cachePath = Path.Combine(dir, "profile.json");
        var cached = TryReadCache(cachePath);
        if (cached is not null && cached.Files.SequenceEqual(manifest))
        {
            return new SpeakerProfile(name, cached.Embedding);
        }

        var embeddings = files
            .Select(f => TryEmbed(f.FullName))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
        if (embeddings.Count == 0)
        {
            logger.LogWarning("No usable enrollment WAVs in {Dir}", dir);
            return null;
        }

        var dim = embeddings[0].Length;
        var mean = new float[dim];
        foreach (var e in embeddings)
        {
            for (var i = 0; i < dim; i++)
            { mean[i] += e[i]; }
        }
        for (var i = 0; i < dim; i++)
        { mean[i] /= embeddings.Count; }
        var profile = OnnxSpeakerEmbedder.L2Normalize(mean);

        try
        {
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheEntry(manifest, profile)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write profile cache {Path}", cachePath);
        }
        logger.LogInformation("Built speaker profile {Name} from {Count} recording(s)", name, embeddings.Count);
        return new SpeakerProfile(name, profile);
    }

    private CacheEntry? TryReadCache(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ignoring unreadable profile cache {Path}", path);
            return null;
        }
    }

    private float[]? TryEmbed(string wavPath)
    {
        try
        {
            var pcm = ReadWav16kMonoS16(wavPath);
            return embedder.Embed(pcm);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skipping enrollment WAV {Path}", wavPath);
            return null;
        }
    }

    // Minimal RIFF parser: accepts only PCM, mono, 16 kHz, 16-bit — anything else throws
    // (and the caller skips the file with a warning naming it).
    private static byte[] ReadWav16kMonoS16(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file");
        }
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file");
        }

        byte[]? data = null;
        var formatOk = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();
            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                var channels = reader.ReadInt16();
                var rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                var bits = reader.ReadInt16();
                reader.BaseStream.Seek(chunkSize - 16, SeekOrigin.Current);
                formatOk = audioFormat == 1 && channels == 1 && rate == 16_000 && bits == 16;
                if (!formatOk)
                {
                    throw new InvalidDataException(
                        $"Need PCM mono 16 kHz 16-bit, got format={audioFormat} ch={channels} rate={rate} bits={bits}");
                }
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }
            else
            {
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }
        }
        return formatOk && data is not null
            ? data
            : throw new InvalidDataException("Missing fmt/data chunk");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpeakerProfileStoreTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Verification/SpeakerProfile.cs McpChannelVoice/Services/Verification/SpeakerProfileStore.cs Tests/Unit/McpChannelVoice/Verification/SpeakerProfileStoreTests.cs
git commit -m "feat(voice): speaker profile store with cached enrollment embeddings"
```

---

### Task 4: Verification settings + SpeakerVerifier policy

**Files:**
- Create: `McpChannelVoice/Settings/SpeakerVerificationSettings.cs`
- Modify: `McpChannelVoice/Settings/VoiceSettings.cs` (add `SpeakerVerification` property)
- Modify: `McpChannelVoice/Settings/SatelliteConfig.cs` (per-satellite overrides)
- Create: `McpChannelVoice/Services/Verification/ISpeakerVerifier.cs`
- Create: `McpChannelVoice/Services/Verification/SpeakerVerifier.cs`
- Create: `Tests/Unit/McpChannelVoice/Verification/SpeakerVerifierTests.cs`

**Interfaces:**
- Consumes: `ISpeakerEmbedder`, `SpeakerProfile`, `OnnxSpeakerEmbedder.Cosine` (Tasks 2-3); `SatelliteConfig` (existing).
- Produces:
  - `record SpeakerVerificationSettings { bool Enabled = false; string ModelPath = "/app/models/speaker-embedding.onnx"; string VoicesPath = "/voices"; double SimilarityThreshold = 0.35; int MinVerifySpeechMs = 800; }`
  - `SatelliteConfig.ResolveVerificationEnabled(SpeakerVerificationSettings)` / `ResolveSimilarityThreshold(SpeakerVerificationSettings)`; `record VerificationOverrides { bool? Enabled; double? SimilarityThreshold; }` on `SatelliteConfig.Verification`.
  - `enum SpeakerDecision { Accepted, Rejected, Skipped, Unavailable }`
  - `readonly record struct SpeakerVerification(SpeakerDecision Decision, double? Similarity = null, string? BestMatch = null)`
  - `ISpeakerVerifier { Task<SpeakerVerification> VerifyAsync(IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct); }`
  - `SpeakerVerifier(SpeakerVerificationSettings settings, Func<(ISpeakerEmbedder Embedder, IReadOnlyList<SpeakerProfile> Profiles)> backendFactory, ILogger<SpeakerVerifier> logger)` — factory invoked lazily once, exceptions → permanently `Unavailable` (fail-open).

- [ ] **Step 1: Add the settings**

`McpChannelVoice/Settings/SpeakerVerificationSettings.cs`:

```csharp
namespace McpChannelVoice.Settings;

public record SpeakerVerificationSettings
{
    // Master switch. Also effectively off while the voices folder holds no profiles.
    public bool Enabled { get; init; }
    public string ModelPath { get; init; } = "/app/models/speaker-embedding.onnx";
    public string VoicesPath { get; init; } = "/voices";
    // Cosine accept bar. Conservative (accept-leaning) until field-tuned; see the spec's
    // calibration notes — the integration test prints real same/cross-speaker scores.
    public double SimilarityThreshold { get; init; } = 0.35;
    // Below this much gate-classified speech the capture skips verification entirely:
    // sub-second embeddings are unreliable and short real commands must stay safe.
    public int MinVerifySpeechMs { get; init; } = 800;
}
```

In `McpChannelVoice/Settings/VoiceSettings.cs`, add alongside the existing section properties (e.g. next to `FollowUp`):

```csharp
    public SpeakerVerificationSettings SpeakerVerification { get; init; } = new();
```

In `McpChannelVoice/Settings/SatelliteConfig.cs`, add to `SatelliteConfig` (below the `Gate` property and its resolvers):

```csharp
    // Per-satellite overrides of the speaker-identity gate. Null inherits the global value.
    public VerificationOverrides? Verification { get; init; }

    public bool ResolveVerificationEnabled(SpeakerVerificationSettings global) =>
        Verification?.Enabled ?? global.Enabled;

    public double ResolveSimilarityThreshold(SpeakerVerificationSettings global) =>
        Verification?.SimilarityThreshold ?? global.SimilarityThreshold;
```

And at the bottom of the file, next to `GateSettings`:

```csharp
public record VerificationOverrides
{
    public bool? Enabled { get; init; }
    public double? SimilarityThreshold { get; init; }
}
```

- [ ] **Step 2: Write the failing tests**

`Tests/Unit/McpChannelVoice/Verification/SpeakerVerifierTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Verification;

public class SpeakerVerifierTests
{
    private sealed class FixedEmbedder(float[] embedding) : ISpeakerEmbedder
    {
        public float[] Embed(ReadOnlySpan<byte> pcmS16Le) => embedding;
    }

    private static readonly float[] FranVoice = OnnxSpeakerEmbedder.L2Normalize([1f, 0f, 0f]);
    private static readonly float[] TvVoice = OnnxSpeakerEmbedder.L2Normalize([0f, 1f, 0f]);

    private static SatelliteConfig Config(VerificationOverrides? overrides = null) =>
        new() { Identity = "household", Room = "office", Verification = overrides };

    private static IReadOnlyList<AudioChunk> Chunks() =>
        [new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard }];

    private static SpeakerVerifier Verifier(
        float[] heardVoice,
        SpeakerVerificationSettings? settings = null,
        IReadOnlyList<SpeakerProfile>? profiles = null) =>
        new(
            settings ?? new SpeakerVerificationSettings { Enabled = true },
            () => (new FixedEmbedder(heardVoice), profiles ?? [new SpeakerProfile("fran", FranVoice)]),
            NullLogger<SpeakerVerifier>.Instance);

    [Fact]
    public async Task VerifyAsync_EnrolledVoice_Accepts()
    {
        var result = await Verifier(FranVoice).VerifyAsync(Chunks(), 2000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Accepted);
        result.Similarity!.Value.ShouldBe(1.0, 1e-5);
        result.BestMatch.ShouldBe("fran");
    }

    [Fact]
    public async Task VerifyAsync_UnknownVoice_Rejects()
    {
        var result = await Verifier(TvVoice).VerifyAsync(Chunks(), 2000, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Rejected);
        result.Similarity!.Value.ShouldBe(0.0, 1e-5);
    }

    [Fact]
    public async Task VerifyAsync_ShortSpeech_SkipsWithoutEmbedding()
    {
        var result = await Verifier(TvVoice).VerifyAsync(Chunks(), 500, Config(), default);

        result.Decision.ShouldBe(SpeakerDecision.Skipped);
        result.Similarity.ShouldBeNull();
    }

    [Fact]
    public async Task VerifyAsync_DisabledGlobally_Skips()
    {
        var verifier = Verifier(TvVoice, new SpeakerVerificationSettings { Enabled = false });

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Skipped);
    }

    [Fact]
    public async Task VerifyAsync_PerSatelliteDisable_OverridesGlobalEnable()
    {
        var config = Config(new VerificationOverrides { Enabled = false });

        (await Verifier(TvVoice).VerifyAsync(Chunks(), 2000, config, default))
            .Decision.ShouldBe(SpeakerDecision.Skipped);
    }

    [Fact]
    public async Task VerifyAsync_PerSatelliteThreshold_Overrides()
    {
        // Similarity 0.0 vs a per-satellite threshold of -1 => accepted.
        var config = Config(new VerificationOverrides { SimilarityThreshold = -1 });

        (await Verifier(TvVoice).VerifyAsync(Chunks(), 2000, config, default))
            .Decision.ShouldBe(SpeakerDecision.Accepted);
    }

    [Fact]
    public async Task VerifyAsync_NoProfiles_IsUnavailable()
    {
        var verifier = Verifier(TvVoice, profiles: []);

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
    }

    [Fact]
    public async Task VerifyAsync_BackendFactoryThrows_IsUnavailableAndDoesNotRetry()
    {
        var calls = 0;
        var verifier = new SpeakerVerifier(
            new SpeakerVerificationSettings { Enabled = true },
            () => { calls++; throw new InvalidOperationException("model missing"); },
            NullLogger<SpeakerVerifier>.Instance);

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
        calls.ShouldBe(1); // fail-open once, never re-tried per capture
    }

    [Fact]
    public async Task VerifyAsync_EmbeddingThrows_IsUnavailable()
    {
        var throwing = new ThrowingEmbedder();
        var verifier = new SpeakerVerifier(
            new SpeakerVerificationSettings { Enabled = true },
            () => ((ISpeakerEmbedder)throwing, [new SpeakerProfile("fran", FranVoice)]),
            NullLogger<SpeakerVerifier>.Instance);

        (await verifier.VerifyAsync(Chunks(), 2000, Config(), default))
            .Decision.ShouldBe(SpeakerDecision.Unavailable);
    }

    private sealed class ThrowingEmbedder : ISpeakerEmbedder
    {
        public float[] Embed(ReadOnlySpan<byte> pcmS16Le) => throw new InvalidOperationException("boom");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpeakerVerifierTests" 2>&1 | tail -5`
Expected: compile error, `SpeakerVerifier` / `ISpeakerVerifier` not found.

- [ ] **Step 4: Implement**

`McpChannelVoice/Services/Verification/ISpeakerVerifier.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Verification;

public enum SpeakerDecision
{
    Accepted,
    Rejected,
    Skipped,
    Unavailable
}

public readonly record struct SpeakerVerification(
    SpeakerDecision Decision, double? Similarity = null, string? BestMatch = null);

public interface ISpeakerVerifier
{
    // speechAudio: the capture's speech-classified chunks; speechMs the gate's speech
    // total. Skipped (disabled / too short) and Unavailable (no model, no profiles,
    // inference failure) both mean "let the capture through" — only Rejected blocks.
    Task<SpeakerVerification> VerifyAsync(
        IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct);
}
```

`McpChannelVoice/Services/Verification/SpeakerVerifier.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Verification;

// Policy layer over the embedder + profiles: skip short captures, compare the capture's
// speech audio against enrolled household profiles, fail open on every error path. The
// backend factory (model load + profile build) runs lazily exactly once; a failure there
// pins the verifier to Unavailable rather than breaking voice.
public sealed class SpeakerVerifier : ISpeakerVerifier
{
    private readonly SpeakerVerificationSettings _settings;
    private readonly Lazy<(ISpeakerEmbedder Embedder, IReadOnlyList<SpeakerProfile> Profiles)?> _backend;

    public SpeakerVerifier(
        SpeakerVerificationSettings settings,
        Func<(ISpeakerEmbedder Embedder, IReadOnlyList<SpeakerProfile> Profiles)> backendFactory,
        ILogger<SpeakerVerifier> logger)
    {
        _settings = settings;
        _backend = new Lazy<(ISpeakerEmbedder, IReadOnlyList<SpeakerProfile>)?>(() =>
        {
            try
            {
                return backendFactory();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Speaker verification unavailable (fail-open)");
                return null;
            }
        });
        Logger = logger;
    }

    private ILogger<SpeakerVerifier> Logger { get; }

    public async Task<SpeakerVerification> VerifyAsync(
        IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct)
    {
        if (!config.ResolveVerificationEnabled(_settings) || speechMs < _settings.MinVerifySpeechMs)
        {
            return new SpeakerVerification(SpeakerDecision.Skipped);
        }

        var backend = _backend.Value;
        if (backend is null || backend.Value.Profiles.Count == 0 || speechAudio.Count == 0)
        {
            return new SpeakerVerification(SpeakerDecision.Unavailable);
        }

        try
        {
            var (embedder, profiles) = backend.Value;
            var pcm = Concat(speechAudio);
            var embedding = await Task.Run(() => embedder.Embed(pcm), ct);
            var best = profiles
                .Select(p => (p.Name, Similarity: OnnxSpeakerEmbedder.Cosine(embedding, p.Embedding)))
                .MaxBy(m => m.Similarity);
            var threshold = config.ResolveSimilarityThreshold(_settings);
            var decision = best.Similarity >= threshold ? SpeakerDecision.Accepted : SpeakerDecision.Rejected;
            return new SpeakerVerification(decision, best.Similarity, best.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Speaker verification failed for this capture (fail-open)");
            return new SpeakerVerification(SpeakerDecision.Unavailable);
        }
    }

    private static byte[] Concat(IReadOnlyList<AudioChunk> chunks)
    {
        var pcm = new byte[chunks.Sum(c => c.Data.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Data.Span.CopyTo(pcm.AsSpan(offset));
            offset += chunk.Data.Length;
        }
        return pcm;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpeakerVerifierTests" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 10`.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Settings/SpeakerVerificationSettings.cs McpChannelVoice/Settings/VoiceSettings.cs McpChannelVoice/Settings/SatelliteConfig.cs McpChannelVoice/Services/Verification/ISpeakerVerifier.cs McpChannelVoice/Services/Verification/SpeakerVerifier.cs Tests/Unit/McpChannelVoice/Verification/SpeakerVerifierTests.cs
git commit -m "feat(voice): speaker verifier policy with per-satellite overrides"
```

---

### Task 5: Capture speech tagging

**Files:**
- Modify: `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`
- Modify: `McpChannelVoice/Services/UtteranceCapture.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs`, `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs`

**Interfaces:**
- Consumes: existing `SilenceGate.Process` / `UtteranceCapture.Feed`.
- Produces: `SilenceGate.LastFrameWasSpeech` (bool, per-Process result of the tracker classification); `UtteranceCapture.SpeechAudio` (`IReadOnlyList<AudioChunk>`, speech-classified chunks only; single-threaded with Feed, read after `Completed`).

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs` (before the closing brace):

```csharp
    [Fact]
    public void LastFrameWasSpeech_TracksPerChunkClassification()
    {
        var gate = NewGate();

        Feed(gate, Silent()); // pre-roll gap seeds the floor
        gate.LastFrameWasSpeech.ShouldBeFalse();
        Feed(gate, Loud());
        gate.LastFrameWasSpeech.ShouldBeTrue();
        Feed(gate, Silent());
        gate.LastFrameWasSpeech.ShouldBeFalse();
    }
```

Append to `Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs` (before the closing brace):

```csharp
    [Fact]
    public async Task SpeechAudio_ContainsOnlySpeechClassifiedChunks()
    {
        var capture = new UtteranceCapture(Gate());

        capture.Feed(Silent()); // pre-roll gap seeds the floor
        capture.Feed(Loud());
        capture.Feed(Loud());
        capture.Feed(Silent());
        capture.Feed(Silent());

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);
        capture.SpeechAudio.Count.ShouldBe(2);
        capture.SpeechAudio.ShouldAllBe(c => c.Data.Length == 3200);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SilenceGateTests|FullyQualifiedName~UtteranceCaptureTests" 2>&1 | tail -5`
Expected: compile errors for `LastFrameWasSpeech` and `SpeechAudio`.

- [ ] **Step 3: Implement**

In `McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs`, add a property next to `EndReason`:

```csharp
    // Classification of the most recent Process frame — lets the capture tag which
    // buffered chunks are speech, so the speaker verifier embeds speech-only audio.
    public bool LastFrameWasSpeech { get; private set; }
```

In `Process`, capture the classification (replace the `if (tracker.IsSpeech(...))` line):

```csharp
        LastFrameWasSpeech = tracker.IsSpeech(rms, duration.TotalMilliseconds);
        if (LastFrameWasSpeech)
```

In `Reset()`, add:

```csharp
        LastFrameWasSpeech = false;
```

In `McpChannelVoice/Services/UtteranceCapture.cs`, add a field next to `_forced` and a property next to `Audio`, and tag in `Feed` right after `gate.Process(...)`:

```csharp
    private readonly List<AudioChunk> _speechAudio = [];
```

```csharp
    // Speech-classified chunks only (per the gate). Feed is single-threaded on the
    // Wyoming read loop; read this after Completed settles.
    public IReadOnlyList<AudioChunk> SpeechAudio => _speechAudio;
```

```csharp
        if (gate.LastFrameWasSpeech)
        {
            _speechAudio.Add(chunk);
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice" 2>&1 | tail -5`
Expected: all pass, no failures anywhere in the voice unit suite.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/SilenceGate.cs McpChannelVoice/Services/UtteranceCapture.cs Tests/Unit/McpChannelVoice/Wyoming/SilenceGateTests.cs Tests/Unit/McpChannelVoice/UtteranceCaptureTests.cs
git commit -m "feat(voice): tag speech-classified chunks on the capture"
```

---

### Task 6: Metrics plumbing (UtteranceRejected, Similarity)

**Files:**
- Modify: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`
- Modify: `Domain/DTOs/Metrics/VoiceEvent.cs`
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs` (dispatcher call site only)
- Modify: `McpChannelVoice/appsettings.json`
- Test: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`

**Interfaces:**
- Consumes: existing `TranscriptDispatcher.DispatchAsync(SatelliteSession, TranscriptionResult, string?, CaptureStats?, CancellationToken)`.
- Produces: `VoiceMetric.UtteranceRejected = 18`; `VoiceEvent.Similarity` (`double?`); `DispatchAsync` gains a required `double? similarity` parameter before the `CancellationToken` and publishes it on both outcomes.

- [ ] **Step 1: Write the failing test change**

In `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, test `DispatchAsync_Dispatched_PublishesCaptureAndWhisperStats`: change the `DispatchAsync` call to pass a similarity and assert it:

```csharp
            new CaptureStats(PeakRms: 4200, FloorRms: 320, SpeechMs: 1800, EndReason: "trailing_silence", TrailingRms: 610),
            0.72,
            default);
```

and add with the other `evt.` assertions:

```csharp
        evt.Similarity.ShouldBe(0.72);
```

Every other `DispatchAsync(...)` call in this test file gains a `null,` argument between the `CaptureStats`/`null` stats argument and the final `default` (the compiler will list them).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests" 2>&1 | tail -5`
Expected: compile error — no overload of `DispatchAsync` takes the extra argument.

- [ ] **Step 3: Implement**

`Domain/DTOs/Metrics/Enums/VoiceMetric.cs` — append after `AlarmOffline = 17` (values are pinned; append-only):

```csharp
    UtteranceRejected = 18
```

`Domain/DTOs/Metrics/VoiceEvent.cs` — add next to `Confidence`:

```csharp
    public double? Similarity { get; init; }
```

`McpChannelVoice/Services/TranscriptDispatcher.cs` — change the signature:

```csharp
    public async Task<bool> DispatchAsync(
        SatelliteSession session,
        TranscriptionResult transcript,
        string? agentId,
        CaptureStats? stats,
        double? similarity,
        CancellationToken ct)
```

and add `Similarity = similarity,` next to `Confidence = transcript.Confidence,` in **both** `VoiceEvent` constructions (the dropped and dispatched publishes).

`McpChannelVoice/Services/WyomingSatelliteHost.cs` — update the dispatcher call in `TranscribeAndDispatchAsync` (the verifier itself arrives in Task 7; pass null for now):

```csharp
            var dispatched = await dispatcher.DispatchAsync(
                session, result, voiceSettings.AgentId, capture.Stats, null, ct);
```

`McpChannelVoice/appsettings.json` — add after the `"FollowUp"` block (keep JSON valid — mind the commas):

```json
    "SpeakerVerification": {
        "Enabled": false,
        "SimilarityThreshold": 0.35,
        "MinVerifySpeechMs": 800
    },
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests" 2>&1 | tail -5`
Expected: all dispatcher tests pass. Then `dotnet build agent.sln 2>&1 | grep -E "error|Build succeeded" | head -3` — must succeed (compiler-driven call-site fixes complete).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/VoiceMetric.cs Domain/DTOs/Metrics/VoiceEvent.cs McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs
git commit -m "feat(voice): UtteranceRejected metric and similarity on voice events"
```

---

### Task 7: Host integration + DI

**Files:**
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Test: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`

**Interfaces:**
- Consumes: `ISpeakerVerifier` / `SpeakerVerification` / `SpeakerDecision` (Task 4), `capture.SpeechAudio` (Task 5), `VoiceMetric.UtteranceRejected` + `DispatchAsync` similarity param (Task 6).
- Produces: `WyomingSatelliteHost` ctor gains trailing optional `ISpeakerVerifier? speakerVerifier = null` (null → verification skipped, existing tests unaffected); production DI registers `ISpeakerVerifier`.

- [ ] **Step 1: Write the failing integration test**

In `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`, first read the existing test `Hub_DialsSatelliteRunsAndStreams_TranscribesAndSendsTranscriptBack` (line ~55) — the new test reuses its fake-satellite scaffolding verbatim, changing only the host construction and the assertions. Add:

```csharp
    private sealed class RejectingVerifier : global::McpChannelVoice.Services.Verification.ISpeakerVerifier
    {
        public Task<global::McpChannelVoice.Services.Verification.SpeakerVerification> VerifyAsync(
            IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct) =>
            Task.FromResult(new global::McpChannelVoice.Services.Verification.SpeakerVerification(
                global::McpChannelVoice.Services.Verification.SpeakerDecision.Rejected, 0.12, null));
    }

    [Fact]
    public async Task Hub_UnknownSpeaker_RejectsCaptureWithoutSttAndPublishesMetric()
    {
        // Copy the connection scaffolding of Hub_DialsSatelliteRunsAndStreams_... :
        // fake satellite listener, run-pipeline + audio-chunk stream (silence, loud,
        // loud, silence, silence), CapturingEmitter, Mock<ISpeechToText>, publisher
        // mock collecting VoiceEvents, registry/sessions/manager/dispatcher exactly
        // as there — then construct the host with the extra verifier argument:
        //   var host = new WyomingSatelliteHost(
        //       new WyomingClientSettings { ... same as the template test ... },
        //       new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = false } },
        //       registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(),
        //       publisher.Object, TimeProvider.System, NullLogger<WyomingSatelliteHost>.Instance,
        //       new RejectingVerifier());
        //
        // Assertions replacing the template's:
        //   - the satellite receives a closing transcript (conversation ended);
        //   - stt.Verify(s => s.TranscribeAsync(...), Times.Never());
        //   - published VoiceEvents contain one with Metric == VoiceMetric.UtteranceRejected,
        //     Outcome == "unknown_speaker" and Similarity == 0.12;
        //   - the CapturingEmitter saw NO message notification.
    }
```

Write the full body by copying the template test and applying exactly those changes — the scaffolding is ~80 lines and already in the file; do not invent a different harness.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Hub_UnknownSpeaker" 2>&1 | tail -5`
Expected: compile error — the host has no verifier constructor parameter yet.

- [ ] **Step 3: Implement the host gate**

`McpChannelVoice/Services/WyomingSatelliteHost.cs`:

Add the trailing ctor parameter (after `ILogger<WyomingSatelliteHost> logger`):

```csharp
    ILogger<WyomingSatelliteHost> logger,
    McpChannelVoice.Services.Verification.ISpeakerVerifier? speakerVerifier = null) : IHostedService
```

Add `using McpChannelVoice.Services.Verification;` to the file's usings, then in `TranscribeAndDispatchAsync`, before the `TranscribeAsync` call:

```csharp
            if (speakerVerifier is not null)
            {
                var verification = await speakerVerifier.VerifyAsync(
                    capture.SpeechAudio, capture.Stats.SpeechMs, session.Config, ct);
                if (verification.Decision == SpeakerDecision.Rejected)
                {
                    logger.LogInformation(
                        "Rejecting capture from {Id}: unknown speaker (similarity {Similarity:F3})",
                        session.SatelliteId, verification.Similarity);
                    var stats = capture.Stats;
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.UtteranceRejected,
                        SatelliteId = session.SatelliteId,
                        Room = session.Config.Room,
                        Identity = session.Config.Identity,
                        Outcome = "unknown_speaker",
                        Similarity = verification.Similarity,
                        PeakRms = stats.PeakRms,
                        SpeechMs = stats.SpeechMs,
                        FloorRms = stats.FloorRms,
                        TrailingRms = stats.TrailingRms,
                        EndReason = stats.EndReason,
                        ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
                    }, ct);
                    return false;
                }
                similarity = verification.Similarity;
            }
```

with `double? similarity = null;` declared just above the block (the ctor parameter type `ISpeakerVerifier?` also becomes unqualified under the new using), and pass `similarity` instead of `null` in the `dispatcher.DispatchAsync(...)` call from Task 6.

- [ ] **Step 4: Register in DI**

In `McpChannelVoice/Modules/ConfigModule.cs`, immediately before `services.AddHostedService<WyomingSatelliteHost>();`:

```csharp
        services.AddSingleton<McpChannelVoice.Services.Verification.ISpeakerVerifier>(sp =>
            new McpChannelVoice.Services.Verification.SpeakerVerifier(
                settings.SpeakerVerification,
                () =>
                {
                    var embedder = new McpChannelVoice.Services.Verification.OnnxSpeakerEmbedder(
                        settings.SpeakerVerification.ModelPath);
                    var profiles = new McpChannelVoice.Services.Verification.SpeakerProfileStore(
                        settings.SpeakerVerification.VoicesPath,
                        embedder,
                        sp.GetRequiredService<ILogger<McpChannelVoice.Services.Verification.SpeakerProfileStore>>()).Load();
                    return (embedder, profiles);
                },
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Verification.SpeakerVerifier>>()));
```

(Add `using McpChannelVoice.Services.Verification;` at the top and drop the qualifiers to match file style.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Integration.McpChannelVoice" 2>&1 | tail -5`
Expected: all voice integration tests pass, including the new rejection test. Then the full voice unit suite: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice" 2>&1 | tail -5` — all pass.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Services/WyomingSatelliteHost.cs McpChannelVoice/Modules/ConfigModule.cs Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs
git commit -m "feat(voice): reject unknown-speaker captures before STT"
```

---

### Task 8: Real-model integration tests + fixtures

**Files:**
- Create: `Tests/Integration/McpChannelVoice/Fixtures/speaker-wavs/{alice,bob}/…` (generated, committed)
- Create: `Tests/Integration/McpChannelVoice/SpeakerVerificationModelTests.cs`
- Modify: `Tests/Tests.csproj` (copy speaker-wavs fixtures)

**Interfaces:**
- Consumes: `OnnxSpeakerEmbedder`, `SpeakerProfileStore` (Tasks 2-3).
- Produces: committed fixture WAVs and the pinned model URL/SHA constants later reused by the Dockerfile (Task 9).

- [ ] **Step 1: Generate two-speaker fixture WAVs with piper**

```bash
mkdir -p Tests/Integration/McpChannelVoice/Fixtures/speaker-wavs/{alice,bob}
cat > /tmp/gen_wavs.sh <<'EOF'
set -e
apt-get update -qq && apt-get install -y -qq ffmpeg > /dev/null
pip install -q piper-tts==1.2.0
mkdir -p /tmp/piper
gen() { # gen <voice> <outfile> <text>
  echo "$3" | piper --model "$1" --download-dir /tmp/piper --data-dir /tmp/piper --output_file /tmp/raw.wav
  # +bitexact suppresses ffmpeg's LIST/INFO metadata chunk, guaranteeing the canonical
  # 44-byte RIFF header the test's Pcm() helper slices off.
  ffmpeg -y -loglevel error -i /tmp/raw.wav -ar 16000 -ac 1 -c:a pcm_s16le -fflags +bitexact "$2"
}
gen en_US-lessac-medium /out/alice/enroll-1.wav "The quick brown fox jumps over the lazy dog near the river bank."
gen en_US-lessac-medium /out/alice/enroll-2.wav "Yesterday the weather station reported sunshine across the valley."
gen en_US-lessac-medium /out/alice/enroll-3.wav "Please remember to water the plants before you leave the house."
gen en_US-lessac-medium /out/alice-probe.wav "Could you tell me the weather forecast for tomorrow afternoon?"
gen es_ES-davefx-medium /out/bob/enroll-1.wav "El rapido zorro marron salta sobre el perro perezoso junto al rio."
gen es_ES-davefx-medium /out/bob/enroll-2.wav "Ayer la estacion meteorologica anuncio sol en todo el valle."
gen es_ES-davefx-medium /out/bob/enroll-3.wav "Recuerda regar las plantas antes de salir de casa."
gen es_ES-davefx-medium /out/bob-probe.wav "Puedes decirme el tiempo que hara manana por la tarde?"
EOF
docker run --rm -v "$PWD/Tests/Integration/McpChannelVoice/Fixtures/speaker-wavs:/out" -v /tmp/gen_wavs.sh:/gen.sh python:3.12-slim bash /gen.sh
ls -la Tests/Integration/McpChannelVoice/Fixtures/speaker-wavs/{alice,bob} Tests/Integration/McpChannelVoice/Fixtures/speaker-wavs/*.wav
```

Expected: 8 WAVs, each roughly 50–200 KB. (If `piper-tts==1.2.0` fails to install on Python 3.12, retry with `python:3.11-slim`.)

In `Tests/Tests.csproj`, extend the fixtures ItemGroup from Task 1:

```xml
    <None Include="Integration/McpChannelVoice/Fixtures/speaker-wavs/**" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 2: Write the model tests (fail first because the helper does not exist)**

`Tests/Integration/McpChannelVoice/SpeakerVerificationModelTests.cs`:

```csharp
using McpChannelVoice.Services.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;

namespace Tests.Integration.McpChannelVoice;

// Exercises the real ONNX speaker-embedding model against the committed piper-voice
// fixtures. Downloads the model once into a temp cache; skips when offline.
public class SpeakerVerificationModelTests(ITestOutputHelper output)
{
    // Same artifact the Dockerfile bakes into the image (Task 9 pins the same SHA).
    public const string ModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_CAM++.onnx";

    private static readonly string CachePath =
        Path.Combine(Path.GetTempPath(), "jackbot-speaker-embedding.onnx");

    private static string FixtureRoot => Path.Combine(
        AppContext.BaseDirectory, "Integration", "McpChannelVoice", "Fixtures", "speaker-wavs");

    private static async Task<string?> TryGetModelAsync()
    {
        if (File.Exists(CachePath) && new FileInfo(CachePath).Length > 5_000_000)
        {
            return CachePath;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var bytes = await http.GetByteArrayAsync(ModelUrl);
            await File.WriteAllBytesAsync(CachePath, bytes);
            return CachePath;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Pcm(string wavPath)
    {
        // Fixture WAVs are canonical 44-byte-header PCM produced by ffmpeg.
        var bytes = File.ReadAllBytes(wavPath);
        return bytes[44..];
    }

    [SkippableFact]
    public async Task Embeddings_SeparateEnrolledSpeakerFromStranger()
    {
        var model = await TryGetModelAsync();
        Skip.If(model is null, "speaker model not downloadable (offline?)");

        using var embedder = new OnnxSpeakerEmbedder(model!);
        var profiles = new SpeakerProfileStore(
            FixtureRoot, embedder, NullLogger<SpeakerProfileStore>.Instance).Load();
        profiles.Count.ShouldBe(2);
        var alice = profiles.Single(p => p.Name == "alice");
        var bob = profiles.Single(p => p.Name == "bob");

        var aliceProbe = embedder.Embed(Pcm(Path.Combine(FixtureRoot, "alice-probe.wav")));
        var bobProbe = embedder.Embed(Pcm(Path.Combine(FixtureRoot, "bob-probe.wav")));

        var aliceSame = OnnxSpeakerEmbedder.Cosine(aliceProbe, alice.Embedding);
        var aliceCross = OnnxSpeakerEmbedder.Cosine(aliceProbe, bob.Embedding);
        var bobSame = OnnxSpeakerEmbedder.Cosine(bobProbe, bob.Embedding);
        var bobCross = OnnxSpeakerEmbedder.Cosine(bobProbe, alice.Embedding);
        output.WriteLine($"alice same={aliceSame:F3} cross={aliceCross:F3}");
        output.WriteLine($"bob   same={bobSame:F3} cross={bobCross:F3}");

        aliceSame.ShouldBeGreaterThan(aliceCross + 0.15);
        bobSame.ShouldBeGreaterThan(bobCross + 0.15);
        aliceSame.ShouldBeGreaterThan(0.35); // ships threshold: enrolled voices must pass it
        bobSame.ShouldBeGreaterThan(0.35);
    }

    [SkippableFact]
    public async Task Verifier_AcceptsEnrolledRejectsStranger_WithRealModel()
    {
        var model = await TryGetModelAsync();
        Skip.If(model is null, "speaker model not downloadable (offline?)");

        using var embedder = new OnnxSpeakerEmbedder(model!);
        var aliceOnly = Path.Combine(Path.GetTempPath(), $"voices-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(Path.Combine(aliceOnly, "alice"));
            foreach (var wav in Directory.EnumerateFiles(Path.Combine(FixtureRoot, "alice")))
            {
                File.Copy(wav, Path.Combine(aliceOnly, "alice", Path.GetFileName(wav)));
            }
            var verifier = new SpeakerVerifier(
                new McpChannelVoice.Settings.SpeakerVerificationSettings { Enabled = true },
                () => (embedder, new SpeakerProfileStore(
                    aliceOnly, embedder, NullLogger<SpeakerProfileStore>.Instance).Load()),
                NullLogger<SpeakerVerifier>.Instance);
            var config = new McpChannelVoice.Settings.SatelliteConfig { Identity = "household", Room = "office" };

            var aliceResult = await verifier.VerifyAsync(
                [Chunk(Pcm(Path.Combine(FixtureRoot, "alice-probe.wav")))], 2000, config, default);
            var bobResult = await verifier.VerifyAsync(
                [Chunk(Pcm(Path.Combine(FixtureRoot, "bob-probe.wav")))], 2000, config, default);

            aliceResult.Decision.ShouldBe(SpeakerDecision.Accepted);
            aliceResult.BestMatch.ShouldBe("alice");
            bobResult.Decision.ShouldBe(SpeakerDecision.Rejected);
        }
        finally
        {
            Directory.Delete(aliceOnly, true);
        }
    }

    private static Domain.DTOs.Voice.AudioChunk Chunk(byte[] pcm) => new()
    {
        Data = pcm,
        Format = Domain.DTOs.Voice.AudioFormat.WyomingStandard
    };
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SpeakerVerificationModelTests" 2>&1 | tail -8`
Expected: both tests PASS (or SKIP if offline — rerun online; they must pass before committing). The printed same/cross similarities are the first real calibration data — if `same` scores land below 0.35, the fallback model is `3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx` from the same release tag; switch `ModelUrl`, rerun, and note the change in the commit message.

- [ ] **Step 4: Commit**

```bash
git add Tests/Integration/McpChannelVoice/Fixtures/speaker-wavs Tests/Integration/McpChannelVoice/SpeakerVerificationModelTests.cs Tests/Tests.csproj
git commit -m "test(voice): real-model speaker verification integration coverage"
```

---

### Task 9: Docker, compose volume, enrollment script

**Files:**
- Modify: `McpChannelVoice/Dockerfile`
- Modify: `DockerCompose/docker-compose.yml` (mcp-channel-voice volumes)
- Create: `scripts/enroll-voice.sh`
- Modify: `docs/superpowers/specs/2026-07-21-speaker-identity-gate-design.md` (record the pinned model + SHA)

**Interfaces:**
- Consumes: `ModelUrl` from Task 8 (must stay identical), `SpeakerVerificationSettings` defaults (`/app/models/speaker-embedding.onnx`, `/voices`).
- Produces: the deployable image + volume + enrollment tooling.

- [ ] **Step 1: Pin the model checksum**

```bash
curl -sL -o /tmp/speaker-model.onnx "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_CAM++.onnx"
ls -la /tmp/speaker-model.onnx   # sanity: > 5 MB
sha256sum /tmp/speaker-model.onnx
```

Record the printed SHA — it is used verbatim in the next step. (If Task 8 switched to the fallback model, download that URL instead.)

- [ ] **Step 2: Bake the model into the image**

In `McpChannelVoice/Dockerfile`, in the `final` stage before `COPY --from=publish`:

```dockerfile
FROM base AS final
WORKDIR /app
ADD --checksum=sha256:<PASTE-THE-SHA-FROM-STEP-1> \
    https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_CAM++.onnx \
    /app/models/speaker-embedding.onnx
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelVoice.dll"]
```

Verify: `docker build -f McpChannelVoice/Dockerfile -t voice-model-test --target final . 2>&1 | tail -3` from the repo root — must succeed. (Requires `base-sdk:latest` to exist locally; if it does not, build it the way `DockerCompose/docker-compose.yml` does, or validate the ADD line alone in a scratch Dockerfile with the same URL/checksum.)

- [ ] **Step 3: Mount the voices volume**

In `DockerCompose/docker-compose.yml`, in the `mcp-channel-voice` service (it currently has no `volumes:` key — add one after `restart: unless-stopped`):

```yaml
    volumes:
      - ./volumes/voices:/voices
```

Validate: `docker compose -f DockerCompose/docker-compose.yml config -q` — no output means valid.

- [ ] **Step 4: Write the enrollment script**

`scripts/enroll-voice.sh`:

```bash
#!/usr/bin/env bash
# Records speaker-enrollment WAVs through the satellite's own mic (domain-matched to
# the AGC chain the identity gate hears) and drops them where the hub's voices volume
# can pick them up. Run ON the satellite Pi.
#
# Usage: enroll-voice.sh <name> [count] [scp-target]
#   name        identity folder (e.g. fran)
#   count       recordings to take (default 5)
#   scp-target  optional, e.g. pi5:/opt/jackbot/DockerCompose/volumes/voices
#               (omit to leave files in ./voices/<name>/ and copy manually)
set -euo pipefail

NAME="${1:?usage: enroll-voice.sh <name> [count] [scp-target]}"
COUNT="${2:-5}"
TARGET="${3:-}"
SECONDS_PER_TAKE=4
OUT="./voices/$NAME"
mkdir -p "$OUT"

CARD=$(arecord -l | awk -F'[:,]' '/^card /{gsub(/^ +| +$/, "", $2); split($2, a, " "); print a[1]; exit}')
[ -n "$CARD" ] || { echo "No capture card found (arecord -l)"; exit 1; }
echo "Recording from card: $CARD"

RESTART_SATELLITE=0
if systemctl is-active --quiet nabu-satellite; then
    echo "Stopping nabu-satellite (it holds the capture device); will restart when done."
    sudo systemctl stop nabu-satellite
    RESTART_SATELLITE=1
fi
trap '[ "$RESTART_SATELLITE" = 1 ] && sudo systemctl start nabu-satellite' EXIT

PHRASES=(
  "Di tu frase de activacion y una orden completa, con voz natural."
  "Pide el tiempo de manana como lo harias normalmente."
  "Pide que ponga tu musica favorita en el salon."
  "Pregunta que hora es y pide un temporizador de cinco minutos."
  "Di una frase larga cualquiera, como si hablaras con el asistente."
)

for i in $(seq 1 "$COUNT"); do
    idx=$(( (i - 1) % ${#PHRASES[@]} ))
    echo
    echo "[$i/$COUNT] ${PHRASES[$idx]}"
    for s in 3 2 1; do echo "  $s..."; sleep 1; done
    echo "  HABLA AHORA (${SECONDS_PER_TAKE}s)"
    arecord -q -D "plughw:CARD=$CARD,DEV=0" -f S16_LE -r 16000 -c 1 \
        -d "$SECONDS_PER_TAKE" "$OUT/enroll-$i.wav"
    echo "  guardado $OUT/enroll-$i.wav"
done

echo
if [ -n "$TARGET" ]; then
    echo "Copying to $TARGET/$NAME/"
    ssh "${TARGET%%:*}" "mkdir -p '${TARGET#*:}/$NAME'"
    scp "$OUT"/enroll-*.wav "$TARGET/$NAME/"
    echo "Done. Restart mcp-channel-voice (or wait for its next start) to rebuild profiles."
else
    echo "Recordings in $OUT — copy them to the hub's DockerCompose/volumes/voices/$NAME/"
fi
```

Then: `chmod +x scripts/enroll-voice.sh && bash -n scripts/enroll-voice.sh` (syntax check — no output on success).

- [ ] **Step 5: Record the pinned model in the spec**

In `docs/superpowers/specs/2026-07-21-speaker-identity-gate-design.md`, in the "Model and runtime" section, append a line stating the final model file name, its release URL, and the pinned SHA256 from Step 1.

- [ ] **Step 6: Full verification**

```bash
dotnet build agent.sln 2>&1 | grep -E "error|Build succeeded" | head -3
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice|FullyQualifiedName~Tests.Integration.McpChannelVoice" 2>&1 | tail -3
```

Expected: build succeeds; every voice test passes.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Dockerfile DockerCompose/docker-compose.yml scripts/enroll-voice.sh docs/superpowers/specs/2026-07-21-speaker-identity-gate-design.md
git commit -m "feat(voice): ship speaker model in image, voices volume, enrollment script"
```

---

## Rollout (manual, after merge — not part of this plan's tasks)

1. Run `scripts/enroll-voice.sh <name>` on the fran-office Pi for each household member; copy WAVs into `DockerCompose/volumes/voices/<name>/` on pi5.
2. Set `SPEAKERVERIFICATION__ENABLED=true` (or `SpeakerVerification.Enabled` in config) for `mcp-channel-voice` on pi5 and rebuild/restart.
3. TV-on test evening; watch `UtteranceRejected` (similarity of TV) vs `UtteranceTranscribed.Similarity` (household scores) on the dashboard API; adjust `SimilarityThreshold` (global or the fran-office `Verification` override) if the distributions demand it.
