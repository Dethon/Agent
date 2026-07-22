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