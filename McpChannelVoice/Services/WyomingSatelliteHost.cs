using System.Diagnostics;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Dials each configured satellite as a Wyoming client. The satellite is itself
// a Wyoming server: it runs local wake detection and, once the wake word fires,
// sends us run-pipeline followed by an open-ended mic audio stream. We segment
// that stream with SilenceGate, transcribe it, and send a transcript back to stop
// streaming and re-arm the satellite. TTS replies flow the other way as
// audio-start/audio-chunk/audio-stop frames driven by the session playback loop.
public sealed class WyomingSatelliteHost(
    WyomingClientSettings settings,
    VoiceSettings voiceSettings,
    SatelliteRegistry satelliteRegistry,
    SatelliteSessionRegistry sessionRegistry,
    VoiceConversationManager conversationManager,
    ISpeechToText speechToText,
    TranscriptDispatcher dispatcher,
    ActiveAlertRegistry alerts,
    IMetricsPublisher metrics,
    TimeProvider time,
    ILogger<WyomingSatelliteHost> logger,
    ISpeakerVerifier? speakerVerifier = null) : IHostedService
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
                "Set Satellites:<id>:Address (env Satellites__<id>__Address, e.g. tcp://host.docker.internal:10800).",
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
        if (_cts is null)
        {
            return;
        }
        await _cts.CancelAsync();
        try
        {
            await Task.WhenAll(_connections);
        }
        catch
        {
            // Connection loops unwind on cancellation; surfaced faults are expected here.
        }
        finally
        {
            _cts.Dispose();
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

        var playbackTask = Task.Run(() => session.RunPlaybackLoopAsync(
            (chunk, jct) => WritePlaybackFrameAsync(client, chunk, jct),
            ct, time, logger,
            onAudioStart: (format, sct) => client.WriteAsync(WyomingEvent.Header("audio-start", new JsonObject
            {
                ["rate"] = format.SampleRateHz,
                ["width"] = format.SampleWidthBytes,
                ["channels"] = format.Channels,
                ["timestamp"] = 0
            }), sct),
            onAudioStop: sct => client.WriteAsync(
                WyomingEvent.Header("audio-stop", new JsonObject { ["timestamp"] = 0 }), sct),
            onError: async (job, ex) =>
            {
                try
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.TtsError,
                        SatelliteId = id,
                        Room = config.Room,
                        Identity = config.Identity,
                        Error = ex.Message,
                        ConversationId = conversationManager.GetActiveConversationId(id)
                    }, ct);
                }
                catch (Exception mex)
                {
                    logger.LogWarning(mex, "Failed to publish TtsError metric for {Id} ({Label})", id, job.Label);
                }
            }), ct);

        var followUp = voiceSettings.FollowUp with
        {
            Enabled = config.FollowUpEnabled ?? voiceSettings.FollowUp.Enabled
        };

        var coordinator = BuildCoordinator(id, config, client, session, followUp);
        var conversationTask = Task.Run(() => coordinator.RunAsync(ct), ct);

        try
        {
            await client.WriteAsync(WyomingEvent.Header("run-satellite", new JsonObject()), ct);

            await foreach (var evt in client.ReadAllAsync(ct))
            {
                switch (evt.Type)
                {
                    case "run-pipeline":
                    case "audio-start":
                        // Waking the satellite during an active alert dismisses it — no spoken command
                        // needed (the satellite mics only on local wake).
                        NoteDismissals(session, alerts.Acknowledge(id));
                        coordinator.OnWake();
                        break;

                    case "audio-chunk":
                        var (rate, width, channels) = FormatOf(evt.Data);
                        session.RouteAudio(ToChunk(evt.Payload, rate, width, channels));
                        break;

                    case "audio-stop":
                        session.EndCapture();
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
            coordinator.Dispose();
            session.CompletePlayback();
            try
            { await playbackTask; }
            catch { /* unwinds on cancellation / disconnect */ }
            try
            { await conversationTask; }
            catch { /* unwinds on cancellation / disconnect */ }
            sessionRegistry.Unregister(id);
        }
    }

    private FollowUpConversation BuildCoordinator(
        string id, SatelliteConfig config, WyomingClient client, SatelliteSession session, FollowUpSettings followUp)
    {
        return new FollowUpConversation(followUp, time)
        {
            OpenCapture = isFollowUp =>
            {
                session.MarkTurnStart(time.GetTimestamp()); // turn opens here; loop reports turn -> first-audio
                if (!isFollowUp)
                {
                    PublishVoiceMetric(VoiceMetric.WakeTriggered, session); // on-device wake started this conversation
                }
                // Same no-speech window on the wake turn as on follow-ups: a wake with no speech
                // (false trigger, user changes their mind) must re-arm after WindowMs instead of
                // holding the mic open until the far-larger max-utterance cap.
                return session.OpenCapture(new SilenceGate(
                    new AdaptiveLevelTracker(
                        config.ResolveRmsThreshold(settings),
                        config.ResolveEnterMarginDb(settings),
                        config.ResolveExitMarginDb(settings),
                        config.ResolvePeakDropDb(settings),
                        TimeSpan.FromMilliseconds(config.ResolveFloorWindowMs(settings)),
                        demoteMarginDb: config.ResolveDemoteMarginDb(settings)),
                    TimeSpan.FromMilliseconds(settings.TrailingSilenceMs),
                    TimeSpan.FromMilliseconds(settings.MaxUtteranceMs),
                    TimeSpan.FromMilliseconds(config.ResolveMinSpeechMs(settings)),
                    noSpeechTimeout: TimeSpan.FromMilliseconds(followUp.WindowMs)));
            },
            CloseCapture = session.CloseCapture,
            TranscribeAndDispatch = (capture, isFollowUp, token) =>
                TranscribeAndDispatchAsync(session, capture, isFollowUp, token),
            EnqueueChime = token => EnqueueChimeAsync(session, token),
            EndConversation = token => client.WriteAsync(
                WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), token),
            ResetTurn = session.ResetTurn,
            AwaitReply = session.WaitForTurnSpokenAsync,
            OnFollowUpWindow = token =>
            {
                PublishVoiceMetric(VoiceMetric.FollowUpWindowOpened, session);
                return Task.CompletedTask;
            },
            OnSilenceTimeout = (stats, token) =>
            {
                PublishVoiceMetric(VoiceMetric.FollowUpTimedOut, session, stats);
                return Task.CompletedTask;
            },
            OnReplyTimeout = token =>
            {
                logger.LogWarning(
                    "Reply handshake timed out for {Id} after {TimeoutMs}ms; ending conversation and re-arming wake",
                    session.SatelliteId, followUp.ReplyTimeoutMs);
                return Task.CompletedTask;
            }
        };
    }

    // Returns true only when the transcript actually reached the agent. Empty/low-confidence
    // transcripts and STT errors return false so the conversation ends and wake re-arms, rather
    // than the loop blocking forever on a reply handshake the agent will never complete.
    private async Task<bool> TranscribeAndDispatchAsync(
        SatelliteSession session, UtteranceCapture capture, bool isFollowUp, CancellationToken ct)
    {
        try
        {
            double? similarity = null;
            if (speakerVerifier is not null)
            {
                var verification = await speakerVerifier.VerifyAsync(
                    capture.BufferedAudio, capture.Stats.SpeechMs, session.Config, ct);
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

            var sw = Stopwatch.StartNew();
            // Honor a per-satellite STT language override (symmetric with the per-satellite
            // Tts.Wyoming.Voice override resolved in SendReplyTool/AnnouncementService); null falls
            // back to the global Stt.Wyoming.Language inside the backend.
            var options = new TranscriptionOptions { Language = session.Config.Stt?.Wyoming?.Language };
            var result = await speechToText.TranscribeAsync(capture.Audio, options, ct);
            sw.Stop();

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttLatencyMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = sw.ElapsedMilliseconds,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            }, ct);

            if (isFollowUp)
            {
                PublishVoiceMetric(VoiceMetric.FollowUpEngaged, session);
            }

            var dispatched = await dispatcher.DispatchAsync(
                session, result, voiceSettings.AgentId, capture.Stats, similarity, ct);
            if (dispatched)
            {
                // Wake (above) is the primary dismissal path; this is a harmless fallback for turns
                // where a wake event was not observed. The registry makes a second Acknowledge a no-op.
                // Runs AFTER this dispatch, so its snooze context lands on the NEXT transcript.
                NoteDismissals(session, alerts.Acknowledge(session.SatelliteId));
            }
            return dispatched;
        }
        catch (OperationCanceledException)
        {
            // Connection tearing down.
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription failed for {Id}", session.SatelliteId);
            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttError,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Error = ex.Message,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            }, ct);
            return false;
        }
    }

    private async Task EnqueueChimeAsync(SatelliteSession session, CancellationToken ct)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new PlaybackJob(
            Label: $"chime:{session.SatelliteId}",
            Priority: AnnouncePriority.High,
            Audio: ListeningChime.Stream(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => { drained.TrySetResult(); return Task.CompletedTask; },
            OnDrained: () => { drained.TrySetResult(); return Task.CompletedTask; },
            OnFailed: _ => { drained.TrySetResult(); return Task.CompletedTask; });

        await session.EnqueuePlaybackAsync(job, voiceSettings.Announce.QueueMaxDepth);
        await drained.Task.WaitAsync(ct);
    }

    private void NoteDismissals(SatelliteSession session, IReadOnlyList<DismissedAlert> dismissed)
    {
        if (dismissed.Count == 0)
        {
            return;
        }
        var description = string.Join(" and ", dismissed.Select(d =>
            $"{d.Kind.ToString().ToLowerInvariant()} \"{d.Text}\""));
        session.NoteDismissedAlert(description, time.GetUtcNow());
    }

    private void PublishVoiceMetric(VoiceMetric metric, SatelliteSession session, CaptureStats? stats = null) =>
        _ = SafePublishAsync(new VoiceEvent
        {
            Metric = metric,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            PeakRms = stats?.PeakRms,
            SpeechMs = stats?.SpeechMs,
            FloorRms = stats?.FloorRms,
            TrailingRms = stats?.TrailingRms,
            EndReason = stats?.EndReason,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        });

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
        WyomingNumber.ReadInt(data, "rate", AudioFormat.WyomingStandard.SampleRateHz),
        WyomingNumber.ReadInt(data, "width", AudioFormat.WyomingStandard.SampleWidthBytes),
        WyomingNumber.ReadInt(data, "channels", AudioFormat.WyomingStandard.Channels)
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