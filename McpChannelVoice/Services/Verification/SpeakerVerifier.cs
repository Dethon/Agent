using Domain.DTOs.Voice;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging;

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
        IReadOnlyList<AudioChunk> captureAudio, long speechMs, SatelliteConfig config, CancellationToken ct,
        bool enforceMinSpeech = true)
    {
        if (!config.ResolveVerificationEnabled(_settings)
            || (enforceMinSpeech && speechMs < _settings.MinVerifySpeechMs))
        {
            return new SpeakerVerification(SpeakerDecision.Skipped);
        }

        var backend = _backend.Value;
        if (backend is null || backend.Value.Profiles.Count == 0 || captureAudio.Count == 0)
        {
            return new SpeakerVerification(SpeakerDecision.Unavailable);
        }

        try
        {
            var (embedder, profiles) = backend.Value;
            var pcm = Concat(captureAudio);
            var embedding = await Task.Run(() => embedder.Embed(pcm), ct);
            var ranked = profiles
                .Select(p => (p.Name, Similarity: p.Prototypes.Max(e => OnnxSpeakerEmbedder.Cosine(embedding, e))))
                .OrderByDescending(m => m.Similarity)
                .ToList();
            var best = ranked[0];
            var decision = best.Similarity >= config.ResolveSimilarityThreshold(_settings)
                ? SpeakerDecision.Accepted
                : SpeakerDecision.Rejected;

            return new SpeakerVerification(
                decision, best.Similarity, best.Name, Identify(best, ranked, decision, config));
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

    // Conclusive identity: name the speaker only when the match is Accepted, clears IdentifyThreshold,
    // and beats the runner-up by IdentifyMargin (auto-satisfied with a single enrolled profile, which
    // has no runner-up). The doubtful band returns null, so the caller keeps the satellite's default
    // identity rather than guess a person.
    private string? Identify(
        (string Name, double Similarity) best,
        IReadOnlyList<(string Name, double Similarity)> ranked,
        SpeakerDecision decision,
        SatelliteConfig config)
    {
        if (decision != SpeakerDecision.Accepted
            || best.Similarity < config.ResolveIdentifyThreshold(_settings))
        {
            return null;
        }
        var runnerUp = ranked.Count > 1 ? ranked[1].Similarity : double.NegativeInfinity;
        return best.Similarity - runnerUp >= config.ResolveIdentifyMargin(_settings) ? best.Name : null;
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