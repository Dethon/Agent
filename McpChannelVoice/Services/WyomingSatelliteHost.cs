using System.Diagnostics;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
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
    WakeArbiter arbiter,
    SilenceGateFactory gates,
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
        // The WyomingClient lives only in this scope, so hand the session a writer for control
        // events raised from outside it — the transcript fast-path and the insistent alert hold.
        session.ControlWriter = (evt, ct2) => client.WriteAsync(evt, ct2);

        // Hoisted so the widened try/finally below can release them even if something in the setup
        // that follows (arbiter registration, the playback/conversation Task.Run calls, building the
        // coordinator) throws synchronously — that setup used to sit OUTSIDE the try, so a throw
        // there left the session registered with a ControlWriter closing over an already-disposed
        // client until the next reconnect replaced it.
        Task? playbackTask = null;
        FollowUpConversation? coordinator = null;
        Task? conversationTask = null;

        try
        {
            sessionRegistry.Register(session);
            // Both re-arm writes share the connection's single WyomingWriter with the playback loop and
            // the coordinator's EndConversation, which already write to it concurrently today — the
            // arbiter is a third caller under the same guarantees, not a new sharing model.
            arbiter.Register(id, new WakeArbiterHandle(
                session,
                ct2 => client.WriteAsync(WyomingEvent.Header("pause-satellite", new JsonObject()), ct2),
                ct2 => client.WriteAsync(
                    WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), ct2)));
            logger.LogInformation("Connected to satellite {Id} at {Host}:{Port}", id, host, port);

            playbackTask = Task.Run(() => session.RunPlaybackLoopAsync(
                (chunk, jct) => WritePlaybackFrameAsync(client, chunk, jct),
                ct, time, logger,
                onAudioStart: (format, alert, sct) => client.WriteAsync(
                    WyomingEvent.Header("audio-start", BuildAudioStart(format, alert)), sct),
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

            // Bound to a non-nullable local for the Task.Run lambda below: the nullable `coordinator`
            // field exists only so the finally can null-conditionally Dispose it, and the compiler
            // can't narrow a captured nullable field's null-state across a lambda boundary.
            var builtCoordinator = BuildCoordinator(id, config, client, session, followUp);
            coordinator = builtCoordinator;
            conversationTask = Task.Run(() => builtCoordinator.RunAsync(ct), ct);

            // Per-turn, and only ever touched from this single read loop: did run-pipeline already
            // announce this turn? Cleared at audio-stop, which is exactly where the satellite ends the
            // mic stream (transcript or pause-satellite both route through its end_capture).
            var wakeAnnounced = false;

            await client.WriteAsync(WyomingEvent.Header("run-satellite", new JsonObject()), ct);

            await foreach (var evt in client.ReadAllAsync(ct))
            {
                switch (evt.Type)
                {
                    // The only frame that carries wake metadata. nabu-satellite sends exactly this
                    // one per turn; other Wyoming satellites may follow it with audio-start.
                    case "run-pipeline":
                        // Waking the satellite during an active alert dismisses it — no spoken command
                        // needed (the satellite mics only on local wake).
                        NoteDismissals(session, alerts.Acknowledge(id));
                        var wake = ReadWakeAnnouncement(evt.Data);
                        if (wake.Rms is not null)
                        {
                            session.MarkSupportsPause();
                        }
                        wakeAnnounced = true;
                        // Recorded before OnWake, which opens the capture on this thread and reads
                        // the memory back through the same factory.
                        gates.RecordRoomLevel(id, wake.RoomRms ?? 0);
                        // Stashed before OnWake, which opens the capture synchronously on this thread
                        // and consumes the stash onto WakeTriggered.
                        session.NoteWakeSignal(wake.Rms, wake.Score);
                        arbiter.Claim(id, wake.Rms, wake.Score, wake.Source);
                        coordinator.OnWake();
                        // OnWake opens the capture on this thread and consumes the stash — unless a
                        // turn was already open, in which case it no-ops and nothing consumes it.
                        // Anything still stashed here therefore belongs to a turn that never used
                        // it, and the next wake would report it as its own loudness. Drop it: a
                        // missing WakeRms reads as "unknown", a wrong one silently skews the
                        // RmsOffsetDb calibration it feeds.
                        session.TryConsumeWakeSignal();
                        break;

                    // Legacy/foreign satellites announce the mic stream with audio-start, so it still
                    // has to open a turn. It carries no wake metadata, and deliberately neither
                    // stashes nor claims once run-pipeline has announced this turn: noting (null,
                    // null) would erase the loudness WakeTriggered reports for RmsOffsetDb
                    // calibration, and a null-rms claim only survives the arbiter's first-wins
                    // in-window dedupe if run-pipeline happens to arrive first — a satellite that
                    // reordered the two would silently lose every steal.
                    case "audio-start":
                        NoteDismissals(session, alerts.Acknowledge(id));
                        if (!wakeAnnounced)
                        {
                            arbiter.Claim(id, null, null, "wake");
                        }
                        coordinator.OnWake();
                        break;

                    case "audio-chunk":
                        var (rate, width, channels) = FormatOf(evt.Data);
                        session.RouteAudio(ToChunk(evt.Payload, rate, width, channels));
                        break;

                    case "audio-stop":
                        wakeAnnounced = false;
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
            // First, before anything that can await: a dropped connection must stop being an
            // arbitration candidate at once. Everything below is unbounded — the playback loop can
            // be parked writing to the very socket that just died — and until this runs the dying
            // session is still a Rule B holder candidate whose capture history is still populated
            // (on the cancellation path FollowUpConversation never reaches CloseCapture). A live
            // satellite waking in that window would be suppressed as a leak in favour of a
            // satellite that is already gone.
            arbiter.Unregister(id);
            coordinator?.Dispose();
            session.CompletePlayback();
            // Null-guarded: setup that failed before reaching the Task.Run call never produced a
            // task to await, and there is nothing to wait out in that case.
            if (playbackTask is not null)
            {
                try
                { await playbackTask; }
                catch { /* unwinds on cancellation / disconnect */ }
            }
            if (conversationTask is not null)
            {
                try
                { await conversationTask; }
                catch { /* unwinds on cancellation / disconnect */ }
            }
            session.ControlWriter = null;
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
                    // on-device wake started this conversation; the read loop stashed what the
                    // satellite reported about it, and this is the single-use consumer. Only the
                    // wake turn: a follow-up has no wake of its own, so consuming there would either
                    // report nothing or, worse, attribute the wake turn's loudness to it.
                    var wake = session.TryConsumeWakeSignal();
                    PublishVoiceMetric(VoiceMetric.WakeTriggered, session,
                        wakeRms: wake?.Rms, wakeScore: wake?.Score);
                }
                return session.OpenCapture(
                    gates.Create(id, config),
                    // Rule B asks an already-open capture, retrospectively, what it heard during
                    // another satellite's wake-word span — so every capture has to remember.
                    new ChunkHistory(time, voiceSettings.Arbitration.HistorySpan));
            },
            CloseCapture = capture =>
            {
                session.CloseCapture();
                gates.RecordCaptureClose(id, capture.Stats);
                // Stamped here rather than inside the session so it uses the host's TimeProvider —
                // the same instance handed to RunPlaybackLoopAsync, which reads it back. The frozen
                // endpointing tail rewinds the close to the instant the user actually stopped
                // talking; read here, at the close, because it is the last point where the tail is
                // known to be the one the gate ended on.
                session.MarkSpeechEnd(time.GetTimestamp(), capture.Stats.TrailingSilenceMs, time);
            },
            TranscribeAndDispatch = (capture, isFollowUp, token) =>
                TranscribeAndDispatchAsync(session, capture, isFollowUp, token),
            EnqueueChime = token => EnqueueChimeAsync(session, token),
            EndConversation = token => client.WriteAsync(
                WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty }), token),
            SpeechStopped = token => client.WriteAsync(
                WyomingEvent.Header("voice-stopped", new JsonObject()), token),
            ListeningStarted = token => client.WriteAsync(
                WyomingEvent.Header("listening-started", new JsonObject()), token),
            ResetTurn = session.Turn.Reset,
            AwaitReply = session.Turn.AwaitSpoken,
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
            },
            EarlyVerifyMs = speakerVerifier is null ? 0 : voiceSettings.SpeakerVerification.EarlyVerifyMs,
            EarlyReject = (capture, token) => EarlyRejectAsync(session, capture, token)
        };
    }

    // Early-close speaker check: verify the audio captured so far and, if it is an unknown voice
    // (e.g. background TV that latched as speech), reject it now so the loop closes the capture
    // instead of holding the mic open to trailing silence / the max-utterance cap. Sub-speech and
    // fail-open cases return false (keep capturing), so enrolled voices are never truncated.
    private async Task<bool> EarlyRejectAsync(
        SatelliteSession session, UtteranceCapture capture, CancellationToken ct)
    {
        if (speakerVerifier is null)
        {
            return false;
        }

        var stats = capture.Stats;
        // A capture still open at the early mark is not necessarily someone speaking — a
        // follow-up window holds the mic open regardless of whether anyone has said anything, so
        // a capture can sit here with zero gate-classified speech (pure room noise). Keep the
        // short-utterance skip: with nothing to embed yet, VerifyAsync returns Skipped rather than
        // judging silence as an unknown voice, so the capture keeps running instead of being
        // rejected on a foregone conclusion. Once real speech (TV or otherwise) has latched, it
        // clears MinVerifySpeechMs on its own and this check still applies to it as before.
        var sw = Stopwatch.StartNew();
        var verification = await speakerVerifier.VerifyAsync(
            capture.BufferedAudio, stats.SpeechMs, session.Config, ct, enforceMinSpeech: true);
        sw.Stop();
        await PublishVerifyLatencyAsync(
            VoiceMetric.SpeakerVerifyEarlyMs, session, sw.ElapsedMilliseconds, verification.Similarity, "early");
        if (verification.Decision != SpeakerDecision.Rejected)
        {
            return false;
        }

        logger.LogInformation(
            "Early-rejecting capture from {Id}: unknown speaker (similarity {Similarity:F3})",
            session.SatelliteId, verification.Similarity);
        await PublishUnknownSpeakerAsync(session, stats, verification.Similarity, "unknown_speaker_early");
        return true;
    }

    // Verification runs before the STT stopwatch starts, so without this the ONNX embedding is pure
    // invisible latency. Diagnostic only: routed through SafePublishAsync because EarlyRejectAsync
    // is awaited from the conversation loop with no catch above it. The two passes report under
    // DIFFERENT metrics — the final inline pass (SpeakerVerifyMs) is additive within the turn
    // decomposition, while the early pass (SpeakerVerifyEarlyMs) runs concurrently with the user
    // still speaking and overlaps the utterance, so averaging them together is meaningless. Outcome
    // still carries "early"/"final" for readers that want it in one place.
    private Task PublishVerifyLatencyAsync(
        VoiceMetric metric, SatelliteSession session, long elapsedMs, double? similarity, string outcome) =>
        SafePublishAsync(new VoiceEvent
        {
            Metric = metric,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            Outcome = outcome,
            DurationMs = elapsedMs,
            Similarity = similarity,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        });

    // Rejection telemetry is diagnostic, not part of the turn contract: this is awaited from the
    // conversation loop (EarlyRejectAsync has no catch above it), so a metrics-backbone outage must
    // be swallowed here or it faults the loop and wedges the satellite until reconnect.
    private Task PublishUnknownSpeakerAsync(
        SatelliteSession session, CaptureStats stats, double? similarity, string outcome) =>
        SafePublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.UtteranceRejected,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            Outcome = outcome,
            Similarity = similarity,
            PeakRms = stats.PeakRms,
            SpeechMs = stats.SpeechMs,
            FloorRms = stats.FloorRms,
            TrailingRms = stats.TrailingRms,
            EndReason = stats.EndReason,
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
        });

    // Returns true only when the transcript actually reached the agent. Empty/low-confidence
    // transcripts and STT errors return false so the conversation ends and wake re-arms, rather
    // than the loop blocking forever on a reply handshake the agent will never complete.
    private async Task<bool> TranscribeAndDispatchAsync(
        SatelliteSession session, UtteranceCapture capture, bool isFollowUp, CancellationToken ct)
    {
        try
        {
            // The endpointing tail is audio-domain time (derived from PCM frame durations), so it is
            // exact and immune to scheduling jitter. Published unconditionally — including on the
            // paths that go on to drop the transcript — because tuning TrailingSilenceMs needs the
            // rejected captures too.
            await SafePublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.EndpointTailMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = capture.Stats.TrailingSilenceMs,
                EndReason = capture.Stats.EndReason,
                ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
            });

            double? similarity = null;
            string? identifiedSpeaker = null;
            SpeakerVerification? verification = null;
            if (speakerVerifier is not null)
            {
                // Follow-up captures skip the short-utterance protection: a follow-up window
                // reopens the mic wake-free beside a talking TV, so a short TV burst must be
                // verified and rejected rather than passed through. First-turn captures keep the
                // skip so a genuinely brief opening command stays safe.
                var verifySw = Stopwatch.StartNew();
                verification = await speakerVerifier.VerifyAsync(
                    capture.BufferedAudio, capture.Stats.SpeechMs, session.Config, ct,
                    enforceMinSpeech: !isFollowUp);
                verifySw.Stop();
                await PublishVerifyLatencyAsync(
                    VoiceMetric.SpeakerVerifyMs, session, verifySw.ElapsedMilliseconds,
                    verification.Value.Similarity, "final");
                if (verification.Value.Decision == SpeakerDecision.Rejected)
                {
                    logger.LogInformation(
                        "Rejecting capture from {Id}: unknown speaker (similarity {Similarity:F3})",
                        session.SatelliteId, verification.Value.Similarity);
                    await PublishUnknownSpeakerAsync(
                        session, capture.Stats, verification.Value.Similarity, "unknown_speaker");
                    return false;
                }
                similarity = verification.Value.Similarity;
                // A conclusive match names the speaker (routed into the Sender for per-person memory);
                // the doubtful band leaves this null so the dispatcher keeps the satellite identity.
                identifiedSpeaker = verification.Value.IdentifiedSpeaker;
                if (similarity is not null)
                {
                    // Accepted scores only reach Redis telemetry; log them too so threshold
                    // calibration doesn't require correlating metrics with transcripts offline.
                    logger.LogInformation(
                        "Accepting capture from {Id}: similarity {Similarity:F3}, speaker {Speaker}",
                        session.SatelliteId, similarity, identifiedSpeaker ?? session.Config.Identity);
                }
            }

            var sw = Stopwatch.StartNew();
            // Honor a per-satellite STT language override (symmetric with the per-satellite
            // Tts.OpenAi.Voice override resolved in SendReplyTool/AnnouncementService); null falls
            // back to the global Stt.OpenAi.Language inside the backend. The factory also derives
            // the TSE hints (target speaker, noise floor) from the speaker gate's verdict.
            var options = TranscriptionOptionsFactory.Create(
                session.SatelliteId, session.Config, verification, capture.Stats);
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

            // Taken BEFORE the dispatch: DispatchAsync does real work inside — GetOrCreateAsync is a
            // full create_conversation MCP round trip on a conversation's first turn (the normal case
            // for a one-shot command, given the 5-minute mapping expiry), plus the channel/message
            // write and an awaited Redis publish. Stamped afterwards, all of that sat in no span at
            // all; stamped here it lands inside AgentRoundTripMs, where it belongs.
            var dispatchStartedAt = time.GetTimestamp();
            var dispatched = await dispatcher.DispatchAsync(
                session, result, voiceSettings.AgentId, capture.Stats, similarity, identifiedSpeaker, ct);
            if (dispatched)
            {
                session.Turn.MarkDispatched(dispatchStartedAt);
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

    private void PublishVoiceMetric(
        VoiceMetric metric, SatelliteSession session, CaptureStats? stats = null,
        double? wakeRms = null, double? wakeScore = null) =>
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
            WakeRms = wakeRms,
            WakeScore = wakeScore,
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

    // `alert` tells the satellite to play this stream on its non-attenuated alert route, bypassing
    // the per-satellite voice level. Emitted on every stream, not only alerts, so a wire trace
    // shows the routing explicitly; a pre-1.5 satellite ignores the unknown field.
    internal static JsonObject BuildAudioStart(AudioFormat format, bool alert) => new()
    {
        ["rate"] = format.SampleRateHz,
        ["width"] = format.SampleWidthBytes,
        ["channels"] = format.Channels,
        ["timestamp"] = 0,
        ["alert"] = alert
    };

    internal readonly record struct WakeAnnouncement(double? Rms, double? Score, string Source, double? RoomRms = null);

    // Wake metadata is peer-supplied and optional: pre-arbitration firmware sends run-pipeline with
    // no data object at all, and Wyoming has no schema to stop a peer sending the wrong types. Every
    // read here has to survive absent, null and wrong-typed values, because an exception on the read
    // loop tears down the satellite connection mid-utterance.
    internal static WakeAnnouncement ReadWakeAnnouncement(JsonObject data) => new(
        JsonNumber.ReadDouble(data, "wake_rms"),
        JsonNumber.ReadDouble(data, "wake_score"),
        data["source"] is JsonValue value
        && value.TryGetValue<string>(out var source)
        && !string.IsNullOrWhiteSpace(source)
            ? source
            : "wake",
        JsonNumber.ReadDouble(data, "room_rms"));

    private static (int Rate, int Width, int Channels) FormatOf(JsonObject data) =>
    (
        JsonNumber.ReadInt(data, "rate", AudioFormat.WyomingStandard.SampleRateHz),
        JsonNumber.ReadInt(data, "width", AudioFormat.WyomingStandard.SampleWidthBytes),
        JsonNumber.ReadInt(data, "channels", AudioFormat.WyomingStandard.Channels)
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