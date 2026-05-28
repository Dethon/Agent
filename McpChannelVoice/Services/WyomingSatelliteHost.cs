using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Dials each configured satellite as a Wyoming client. wyoming-satellite is itself
// a Wyoming server: it runs local wake detection and, once the wake word fires,
// sends us run-pipeline followed by an open-ended mic audio stream. We segment
// that stream with SilenceGate, transcribe it, and send a transcript back to stop
// streaming and re-arm the satellite. TTS replies flow the other way as
// audio-start/audio-chunk/audio-stop frames driven by the session playback loop.
public sealed class WyomingSatelliteHost(
    WyomingClientSettings settings,
    SatelliteRegistry satelliteRegistry,
    SatelliteSessionRegistry sessionRegistry,
    ISpeechToText speechToText,
    TranscriptDispatcher dispatcher,
    IMetricsPublisher metrics,
    ILogger<WyomingSatelliteHost> logger) : IHostedService
{
    private CancellationTokenSource? _cts;
    private readonly List<Task> _connections = [];

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var dialable = satelliteRegistry.GetAllIds()
            .Select(id => (Id: id, Config: satelliteRegistry.GetById(id)!))
            .Where(s => !string.IsNullOrWhiteSpace(s.Config.Address))
            .ToList();

        if (dialable.Count == 0)
        {
            logger.LogWarning(
                "No satellites with an Address configured ({Total} known) — the hub will not dial any satellite. " +
                "Set Voice:Satellites:<id>:Address (e.g. tcp://host.docker.internal:10800).",
                satelliteRegistry.GetAllIds().Count);
        }
        else
        {
            logger.LogInformation("Dialing {Count} satellite(s): {Ids}",
                dialable.Count, string.Join(", ", dialable.Select(s => s.Id)));
        }

        foreach (var (id, config) in dialable)
        {
            if (!TryParseAddress(config.Address!, out var host, out var port))
            {
                logger.LogError("Satellite {Id} has invalid address '{Address}', skipping", id, config.Address);
                continue;
            }
            _connections.Add(Task.Run(() => ConnectionLoopAsync(id, config, host, port, token), token));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }
        try
        {
            await Task.WhenAll(_connections);
        }
        catch
        {
            // Connection loops unwind on cancellation; surfaced faults are expected here.
        }
    }

    private async Task ConnectionLoopAsync(string id, SatelliteConfig config, string host, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(id, config, host, port, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Satellite {Id} connection to {Host}:{Port} dropped", id, host, port);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.ReconnectDelaySeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunConnectionAsync(string id, SatelliteConfig config, string host, int port, CancellationToken ct)
    {
        await using var client = new WyomingClient();
        await client.ConnectAsync(host, port, ct);

        var session = new SatelliteSession(id, config);
        sessionRegistry.Register(session);
        logger.LogInformation("Connected to satellite {Id} at {Host}:{Port}", id, host, port);

        var playbackTask = Task.Run(
            () => session.RunPlaybackLoopAsync((chunk, jct) => WritePlaybackFrameAsync(client, chunk, jct), ct), ct);

        Channel<AudioChunk>? utterance = null;
        SilenceGate? gate = null;

        try
        {
            await client.WriteAsync(WyomingEvent.Header("run-satellite", new JsonObject()), ct);

            await foreach (var evt in client.ReadAllAsync(ct))
            {
                switch (evt.Type)
                {
                    case "run-pipeline":
                    case "audio-start" when utterance is null:
                        (utterance, gate) = BeginUtterance(client, session, ct);
                        break;

                    case "audio-chunk" when utterance is not null:
                        var (rate, width, channels) = FormatOf(evt.Data);
                        var decision = gate!.Process(evt.Payload.Span, rate, width, channels);
                        await utterance.Writer.WriteAsync(ToChunk(evt.Payload, rate, width, channels), ct);
                        if (decision == SilenceGate.Decision.EndUtterance)
                        {
                            utterance.Writer.TryComplete();
                            utterance = null;
                            gate = null;
                        }
                        break;

                    case "audio-stop" when utterance is not null:
                        utterance.Writer.TryComplete();
                        utterance = null;
                        gate = null;
                        break;

                    case "error":
                        logger.LogWarning("Satellite {Id} reported error: {Message}",
                            id, evt.Data["text"]?.GetValue<string>());
                        break;
                }
            }
        }
        finally
        {
            utterance?.Writer.TryComplete();
            session.CompletePlayback();
            try
            {
                await playbackTask;
            }
            catch
            {
                // Playback loop unwinds on cancellation / disconnect.
            }
            sessionRegistry.Unregister(id);
        }
    }

    private (Channel<AudioChunk> Utterance, SilenceGate Gate) BeginUtterance(
        WyomingClient client, SatelliteSession session, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AudioChunk>();
        var gate = new SilenceGate(
            settings.SilenceRmsThreshold,
            TimeSpan.FromMilliseconds(settings.TrailingSilenceMs),
            TimeSpan.FromMilliseconds(settings.MaxUtteranceMs),
            TimeSpan.FromMilliseconds(settings.MinSpeechMs));

        _ = Task.Run(() => metrics.PublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.WakeTriggered,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            WakeWord = session.Config.WakeWord,
            ConversationId = session.ConversationId
        }, ct), ct);

        _ = Task.Run(() => TranscribeAndReplyAsync(client, session, channel.Reader, ct), ct);
        return (channel, gate);
    }

    private async Task TranscribeAndReplyAsync(
        WyomingClient client, SatelliteSession session, ChannelReader<AudioChunk> reader, CancellationToken ct)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await speechToText.TranscribeAsync(reader.ReadAllAsync(ct), new TranscriptionOptions(), ct);
            sw.Stop();

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttLatencyMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = sw.ElapsedMilliseconds,
                Language = result.Language,
                ConversationId = session.ConversationId
            }, ct);

            // Stop the satellite streaming and re-arm wake detection.
            await client.WriteAsync(
                WyomingEvent.Header("transcript", new JsonObject { ["text"] = result.Text ?? string.Empty }), ct);

            await dispatcher.DispatchAsync(session, result, agentId: null, ct);
        }
        catch (OperationCanceledException)
        {
            // Connection tearing down; nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription failed for {Id}", session.SatelliteId);
            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttError,
                SatelliteId = session.SatelliteId,
                Error = ex.Message,
                ConversationId = session.ConversationId
            }, ct);

            // Release the satellite even on failure so it re-arms instead of streaming forever.
            try
            {
                await client.WriteAsync(
                    WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), ct);
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static async Task WritePlaybackFrameAsync(WyomingClient client, AudioChunk chunk, CancellationToken ct)
    {
        var data = new JsonObject
        {
            ["rate"] = chunk.Format.SampleRateHz,
            ["width"] = chunk.Format.SampleWidthBytes,
            ["channels"] = chunk.Format.Channels
        };
        await client.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data, chunk.Data), ct);
    }

    private static AudioChunk ToChunk(ReadOnlyMemory<byte> payload, int rate, int width, int channels) => new()
    {
        Data = payload,
        Format = new AudioFormat { SampleRateHz = rate, SampleWidthBytes = width, Channels = channels },
        Timestamp = TimeSpan.Zero
    };

    private static (int Rate, int Width, int Channels) FormatOf(JsonObject data) =>
    (
        data["rate"]?.GetValue<int>() ?? AudioFormat.WyomingStandard.SampleRateHz,
        data["width"]?.GetValue<int>() ?? AudioFormat.WyomingStandard.SampleWidthBytes,
        data["channels"]?.GetValue<int>() ?? AudioFormat.WyomingStandard.Channels
    );

    private static bool TryParseAddress(string address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Port <= 0 || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }
        host = uri.Host;
        port = uri.Port;
        return true;
    }
}