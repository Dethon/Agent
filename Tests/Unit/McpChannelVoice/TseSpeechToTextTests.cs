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
            if (metricEvent is VoiceEvent voice)
            {
                Events.Add(voice);
            }

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