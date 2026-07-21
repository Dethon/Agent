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
    public async Task VerifyAsync_ShortSpeech_MinSpeechNotEnforced_StillVerifies()
    {
        // The early-close check judges a still-running capture on its continuous audio, so it opts
        // out of the short-utterance skip: sub-MinVerifySpeechMs speech must still be verified.
        var result = await Verifier(TvVoice).VerifyAsync(Chunks(), 500, Config(), default, enforceMinSpeech: false);

        result.Decision.ShouldBe(SpeakerDecision.Rejected);
        result.Similarity!.Value.ShouldBe(0.0, 1e-5);
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