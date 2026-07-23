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
            await PublishAsync(VoiceMetric.TseSkipped, options, outcome: skip);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        var mixture = WavCodec.Encode(chunks);
        var stopwatch = Stopwatch.StartNew();
        var reply = await client.ExtractAsync(mixture, options.TargetSpeaker!, ct);
        stopwatch.Stop();

        if (reply is null)
        {
            logger.LogWarning("TSE sidecar unavailable for {Speaker}; raw audio proceeds", options.TargetSpeaker);
            await PublishAsync(VoiceMetric.TseFailed, options, outcome: "unavailable", durationMs: stopwatch.ElapsedMilliseconds);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        AudioChunk extracted;
        try
        {
            extracted = WavCodec.Decode(reply);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "TSE reply malformed for {Speaker}; raw audio proceeds", options.TargetSpeaker);
            await PublishAsync(VoiceMetric.TseFailed, options, outcome: "malformed", durationMs: stopwatch.ElapsedMilliseconds);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        if (extracted.Data.Length == 0)
        {
            logger.LogWarning("TSE reply empty for {Speaker}; raw audio proceeds", options.TargetSpeaker);
            await PublishAsync(VoiceMetric.TseFailed, options, outcome: "empty", durationMs: stopwatch.ElapsedMilliseconds);
            return await inner.TranscribeAsync(Replay(chunks), options, ct);
        }

        await PublishAsync(VoiceMetric.TseInvoked, options, outcome: "ok");
        await PublishAsync(VoiceMetric.TseLatencyMs, options, durationMs: stopwatch.ElapsedMilliseconds);
        audit.Record(
            options.TargetSpeaker!, options.SatelliteId, options.NoiseFloorRms,
            stopwatch.ElapsedMilliseconds, mixture, reply);
        return await inner.TranscribeAsync(Replay(Rechunk(extracted, chunks)), options, ct);
    }

    // WavCodec.Decode hands back the whole reply as one contiguous chunk. Feeding that
    // straight to the inner STT starves SegmentedSpeechToText's gate: with a single frame,
    // AdaptiveLevelTracker's smoothing/min-window each hold exactly one entry, so the frame
    // becomes its own noise floor and IsSpeech can never clear floor + EnterMarginDb. Re-slice
    // the extracted audio to the original capture's frame cadence so the gate sees the same
    // rhythm it was calibrated on. The extracted payload may legitimately be shorter than the
    // mixture (the sidecar clamps its output) — stop once it is exhausted rather than assuming
    // matching lengths.
    private static IReadOnlyList<AudioChunk> Rechunk(AudioChunk extracted, IReadOnlyList<AudioChunk> original)
    {
        var frames = new List<AudioChunk>();
        var offset = 0;
        foreach (var chunk in original)
        {
            if (offset >= extracted.Data.Length)
            {
                break;
            }
            var length = Math.Min(chunk.Data.Length, extracted.Data.Length - offset);
            frames.Add(extracted with { Data = extracted.Data.Slice(offset, length) });
            offset += length;
        }
        if (offset < extracted.Data.Length)
        {
            frames.Add(extracted with { Data = extracted.Data.Slice(offset) });
        }
        return frames;
    }

    private string? SkipReason(TranscriptionOptions options) =>
        options.TargetSpeaker is null ? "no_speaker"
        : settings.Mode == TseMode.Auto && (options.NoiseFloorRms ?? 0) < settings.NoiseFloorThreshold ? "quiet"
        : null;

    private Task PublishAsync(
        VoiceMetric metric, TranscriptionOptions options, string? outcome = null, long? durationMs = null) =>
        SafePublishAsync(new VoiceEvent
        {
            Metric = metric,
            SatelliteId = options.SatelliteId,
            Room = options.Room,
            Identity = options.TargetSpeaker,
            Outcome = outcome,
            DurationMs = durationMs,
            FloorRms = options.NoiseFloorRms
        });

    // Metrics are diagnostic, not part of the fail-open contract: a publish failure must
    // never abort the turn. Always publishes with CancellationToken.None (matching the
    // WyomingSatelliteHost.SafePublishAsync precedent) so this catch-all can never mask a
    // genuine caller-requested cancellation of the turn itself — that only ever surfaces
    // from audio enumeration, client.ExtractAsync, or inner.TranscribeAsync observing ct.
    private async Task SafePublishAsync(VoiceEvent evt)
    {
        try
        {
            await metrics.PublishAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish voice metric {Metric}", evt.Metric);
        }
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