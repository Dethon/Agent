using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class WyomingServer(
    WyomingServerSettings settings,
    SatelliteRegistry satelliteRegistry,
    SatelliteSessionRegistry sessionRegistry,
    ISpeechToText speechToText,
    TranscriptDispatcher dispatcher,
    IMetricsPublisher metrics,
    ILogger<WyomingServer> logger) : IHostedService
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int BoundPort => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? 0;

    public Task StartAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Parse(settings.Host), settings.Port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        logger.LogInformation("Wyoming server listening on {Host}:{Port}", settings.Host, BoundPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            await _acceptLoop;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        var reader = new WyomingReader(stream);
        SatelliteSession? session = null;

        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                if (evt.Type == "info")
                {
                    var name = evt.Data["satellite"]?["name"]?.GetValue<string>()
                               ?? throw new InvalidDataException("info missing satellite.name");
                    var cfg = satelliteRegistry.GetById(name)
                              ?? throw new InvalidOperationException($"Unknown satellite '{name}'");
                    session = new SatelliteSession(name, cfg);
                    sessionRegistry.Register(session);
                    logger.LogInformation("Satellite {Id} connected (identity={Identity})", name, cfg.Identity);

                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.WakeTriggered,
                        SatelliteId = session.SatelliteId,
                        Room = cfg.Room,
                        Identity = cfg.Identity,
                        WakeWord = cfg.WakeWord,
                        ConversationId = session.ConversationId
                    }, ct);

                    _ = Task.Run(() => RunTranscriptionAsync(session, ct), ct);
                    continue;
                }

                if (session is null)
                {
                    continue;
                }

                if (evt.Type == "audio-start")
                {
                    continue;
                }

                if (evt.Type == "audio-chunk")
                {
                    await session.PublishAudioAsync(evt.Payload, ct);
                    continue;
                }

                if (evt.Type == "audio-stop")
                {
                    session.CompleteInboundAudio();
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Wyoming client");
        }
        finally
        {
            if (session is not null)
            {
                sessionRegistry.Unregister(session.SatelliteId);
            }
            client.Dispose();
        }
    }

    private async Task RunTranscriptionAsync(SatelliteSession session, CancellationToken ct)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await speechToText.TranscribeAsync(Stream(session, ct), new TranscriptionOptions(), ct);
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

            await dispatcher.DispatchAsync(session, result, agentId: null, ct);
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
        }
    }

    private static async IAsyncEnumerable<AudioChunk> Stream(
        SatelliteSession session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var bytes in session.ReadInboundAudioAsync(ct))
        {
            yield return new AudioChunk
            {
                Data = bytes,
                Format = AudioFormat.WyomingStandard,
                Timestamp = TimeSpan.Zero
            };
        }
    }
}