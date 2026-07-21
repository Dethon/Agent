using Domain.DTOs.Voice;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit.Abstractions;

namespace Tests.Integration.McpChannelVoice;

// Exercises the real ONNX speaker-embedding model against the committed piper-voice
// fixtures. Downloads the model once into a temp cache; skips when offline.
public class SpeakerVerificationModelTests(ITestOutputHelper output)
{
    // Same artifact the Dockerfile bakes into the image (same URL, same SHA pin).
    public const string ModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2netv2_sv_zh-cn_16k-common.onnx";

    // Model-specific cache name: a stale cache from a previous model must not be reused.
    private static readonly string _cachePath =
        Path.Combine(Path.GetTempPath(), "jackbot-speaker-embedding-eres2netv2.onnx");

    private static string FixtureRoot => Path.Combine(
        AppContext.BaseDirectory, "Integration", "McpChannelVoice", "Fixtures", "speaker-wavs");

    private static async Task<string?> TryGetModelAsync()
    {
        if (File.Exists(_cachePath) && new FileInfo(_cachePath).Length > 5_000_000)
        {
            return _cachePath;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var bytes = await http.GetByteArrayAsync(ModelUrl);
            await File.WriteAllBytesAsync(_cachePath, bytes);
            return _cachePath;
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

        var aliceSame = MaxCosine(aliceProbe, alice);
        var aliceCross = MaxCosine(aliceProbe, bob);
        var bobSame = MaxCosine(bobProbe, bob);
        var bobCross = MaxCosine(bobProbe, alice);
        output.WriteLine($"alice same={aliceSame:F3} cross={aliceCross:F3}");
        output.WriteLine($"bob   same={bobSame:F3} cross={bobCross:F3}");

        aliceSame.ShouldBeGreaterThan(aliceCross + 0.15);
        bobSame.ShouldBeGreaterThan(bobCross + 0.15);
        aliceSame.ShouldBeGreaterThan(0.6); // ships threshold: enrolled voices must pass it
        bobSame.ShouldBeGreaterThan(0.6);
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
            // 0.7 sits cleanly in the measured ERes2NetV2 gap for these fixtures
            // (same ~0.92-0.96, cross ~0.26-0.27). The SHIPPED default in
            // SpeakerVerificationSettings is 0.6, refined further per satellite in the field.
            var verifier = new SpeakerVerifier(
                new SpeakerVerificationSettings { Enabled = true, SimilarityThreshold = 0.7 },
                () => (embedder, new SpeakerProfileStore(
                    aliceOnly, embedder, NullLogger<SpeakerProfileStore>.Instance).Load()),
                NullLogger<SpeakerVerifier>.Instance);
            var config = new SatelliteConfig { Identity = "household", Room = "office" };

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

    // Mirrors production scoring: a profile scores as its best prototype.
    private static double MaxCosine(float[] probe, SpeakerProfile profile) =>
        profile.Prototypes.Max(e => OnnxSpeakerEmbedder.Cosine(probe, e));

    private static AudioChunk Chunk(byte[] pcm) => new()
    {
        Data = pcm,
        Format = AudioFormat.WyomingStandard
    };
}