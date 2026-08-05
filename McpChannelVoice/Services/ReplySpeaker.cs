using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// The one thing that turns an agent's answer into audio on a satellite: deciding what is a speakable
// segment, having it synthesised, queueing it, and reporting how it went. It serves a live answer
// and one delivered to a satellite that was not listening when it was written; both live here
// because they share the accumulator, and splitting by branch would put one collaborator in two
// places.
public sealed class ReplySpeaker(
    ReplyTextAccumulator accumulator,
    ITextToSpeech tts,
    VoiceSettings settings,
    IMetricsPublisher metrics,
    // Only the live path stamps enqueue timing. It used to be resolved from the container on that
    // branch alone so the scheduled path never paid for it; held here it is resolved once for the
    // process instead of once per reply chunk, which is cheaper still.
    TimeProvider time,
    ILogger<ReplySpeaker> logger)
{
    // Which turn each live reply stream opened against, keyed by conversation. The hub can give a
    // turn up at ReplyTimeoutMs and dispatch the next one while the agent is still writing the
    // abandoned answer; that answer's StreamComplete then arrives against a turn it knows nothing
    // about. One conversation's chunks arrive strictly in order (see ReplyTextAccumulator), so the
    // turn that was live when the stream's first event landed is the turn its end belongs to.
    private readonly ConcurrentDictionary<string, StreamToken> _streams = new();

    public void SpeakUtteranceReply(SatelliteSession session, SendReplyParams p)
    {
        var stream = _streams.GetOrAdd(p.ConversationId, _ => session.Turn.OpenStream());

        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
                return;

            // The agent is told to say one word ("Buscando") before slow multi-tool work so the user
            // hears that something started. Text chunks are buffered until StreamComplete, so without
            // this flush that word is spoken glued to the front of the answer — after the wait it
            // exists to cover, costing words and buying nothing. The first tool call of the turn is
            // the moment the wait becomes real, so speak it here. It is a cue, not the reply: it must
            // not resolve the turn handshake (that would end FollowUpConversation and re-arm the mic
            // mid-turn) and it must not publish the reply-latency metrics, which measure time-to-answer.
            case ReplyContentType.ToolCall:
                if (session.Turn.TryClaimPreamble())
                {
                    FlushAndSpeak(session, p.ConversationId, isReply: false);
                }
                return;

            case ReplyContentType.Error:
                // Treat the error as terminal reply text: append it so any buffered partial answer
                // and the error are spoken together, in order, by the trailing StreamComplete —
                // not the error first with the leftover partial spoken after it. Mirrors the
                // flush-on-error contract honored by the Telegram/ServiceBus channels and voice's
                // own scheduled path. (ChatMonitor sends Error with isComplete=false then a
                // StreamComplete; the isComplete guard only covers a transport that completes early.)
                accumulator.Append(p.ConversationId, $" Hubo un error: {p.Content}");
                if (p.IsComplete)
                {
                    // An error the transport itself flags as complete IS the end of the answer, so
                    // the stream ends here exactly as it does on StreamComplete. Left open, the turn
                    // never learns the agent stopped sending and the mic stays shut for the whole
                    // ~120 s reply timeout.
                    FlushAndSpeak(session, p.ConversationId);
                    _streams.TryRemove(p.ConversationId, out _);
                    stream.End();
                }
                return;

            // Completion arrives as a dedicated StreamComplete event (empty content, no
            // messageId). Text chunks are never flagged complete, so this is where we
            // speak the accumulated reply.
            case ReplyContentType.StreamComplete:
                // Speak whatever tail is left after the streamed segments, then tell the turn the
                // agent has stopped sending. Whether that settles the turn silent or leaves it
                // waiting on audio still playing is the turn's decision: streaming may already have
                // spoken everything, leaving this flush empty.
                FlushAndSpeak(session, p.ConversationId);
                _streams.TryRemove(p.ConversationId, out _);
                stream.End();
                return;

            default:
                accumulator.Append(p.ConversationId, p.Content);
                // Defensive: honor an explicitly-completed text chunk if a transport ever sends one.
                if (p.IsComplete)
                {
                    FlushAndSpeak(session, p.ConversationId);
                    _streams.TryRemove(p.ConversationId, out _);
                    stream.End();
                    return;
                }
                SpeakReadySegments(session, p.ConversationId);
                return;
        }
    }

    public async Task DeliverScheduledAsync(
        SendReplyParams p,
        AnnounceTarget target,
        VoiceDeliveryRegistry delivery,
        AnnouncementService announcer)
    {
        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
            case ReplyContentType.ToolCall:
                return;

            // An unsolicited scheduled delivery prefers silence over announcing a failure
            // (e.g. at night). Drop the buffer and the binding without speaking.
            case ReplyContentType.Error:
                accumulator.Flush(p.ConversationId);
                delivery.Remove(p.ConversationId);
                logger.LogWarning("Scheduled voice delivery {ConversationId} errored; not speaking", p.ConversationId);
                return;

            case ReplyContentType.StreamComplete:
                await AnnounceAccumulatedAsync(p.ConversationId, target, delivery, announcer);
                return;

            default:
                accumulator.Append(p.ConversationId, p.Content);
                if (p.IsComplete)
                {
                    await AnnounceAccumulatedAsync(p.ConversationId, target, delivery, announcer);
                }
                return;
        }
    }

    private async Task AnnounceAccumulatedAsync(
        string conversationId,
        AnnounceTarget target,
        VoiceDeliveryRegistry delivery,
        AnnouncementService announcer)
    {
        var text = accumulator.Flush(conversationId);
        delivery.Remove(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await announcer.AnnounceAsync(new AnnounceRequest { Target = target, Text = text }, default);
        }
        catch (AnnounceTargetNotFoundException ex)
        {
            logger.LogWarning(ex, "Scheduled voice delivery {ConversationId} had no matching satellites", conversationId);
        }
    }

    private void FlushAndSpeak(SatelliteSession session, string conversationId, bool isReply = true)
    {
        var text = accumulator.Flush(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        Speak(session, text, conversationId, isReply);
    }

    // Drains every complete sentence run the buffer now holds into the playback queue, so the user
    // hears the answer's opening while the agent is still generating its end. The first run clears a
    // deliberately low bar (it is the wait everyone feels); later ones need more text, because each
    // is its own TTS request and the audio already playing is covering them.
    private void SpeakReadySegments(SatelliteSession session, string conversationId)
    {
        var streaming = settings.Tts.Streaming;
        if (!streaming.Enabled)
        {
            return;
        }

        while (true)
        {
            // Asked before the text is taken, which is worth doing — a segment refused for depth
            // costs a consumed dispatch stamp and its metrics — but it is not the guarantee: it
            // answers for the limit only, and it answers in advance. What guarantees no text is lost
            // is the refusal below handing the run back, because TryTakeSpeakable has already
            // removed it from the buffer by then.
            if (!session.Playback.CanAccept(PlaybackKind.Reply))
            {
                return;
            }

            var minChars = session.Turn.NextSegmentIsFirst
                ? streaming.FirstSegmentMinChars
                : streaming.MinChars;
            if (!accumulator.TryTakeSpeakable(conversationId, minChars, out var segment))
            {
                return;
            }

            if (Speak(session, segment, conversationId, isReply: true) is not null)
            {
                // Back in the buffer for the next chunk, or the StreamComplete flush, to speak.
                // The flush itself never puts anything back: nothing would follow it, so the text
                // would sit there until some later turn spoke it out of nowhere.
                accumulator.PutBack(conversationId, segment);
                return;
            }
        }
    }

    // Synchronous, because queueing is: the segment's synthesis is handed to the queue rather than
    // started here, and the queue answers immediately. Returns why the queue turned the job away, so
    // the streaming path can hand the text it spent back.
    private RefusalReason? Speak(SatelliteSession session, string text, string conversationId, bool isReply)
    {
        var options = new SynthesisOptions { Voice = session.ResolveVoice(settings) };

        // Assigned by BeginSegment below, before the enqueue and therefore before any callback can
        // run. The token carries the epoch it was registered under, so the count and its release
        // cannot land on different turns. A preamble job never registers one and never releases it.
        var segment = default(SegmentToken);

        // Reply text arriving here closes the hub-visible agent round trip: dispatch -> answer.
        // Compared against the agent's own MemoryRecall + LlmTotal, the difference is queue time.
        // TryConsumeDispatchedAt is single-use and consumed here regardless of whether the publish
        // below succeeds, so a metrics blip can never leave the stamp behind for some later, unrelated
        // reply on this session (e.g. a schedule firing into a satellite that had a real turn earlier)
        // to pick up and report as its own invented round trip. The consumed value doubles as the
        // proof that this reply answers a transcript this hub dispatched, which the turn-anchored
        // spans below need — see the OnFirstAudio gate.
        var dispatchedAtStamp = isReply ? session.Turn.TryConsumeDispatchedAt() : null;

        // The end bound of the agent round trip and the start of the queue wait, so the two spans
        // meet exactly.
        var enqueuedAt = time.GetTimestamp();

        if (dispatchedAtStamp is { } dispatchedAt)
        {
            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.AgentRoundTripMs,
                DurationMs = (long)time.GetElapsedTime(dispatchedAt, enqueuedAt).TotalMilliseconds,
                ConversationId = conversationId
            }.About(session));
        }

        // Latency is still measured in the loop, and still means the same thing: for the first reply
        // segment the loop pulls immediately, so it observes the real synthesis time. Later segments
        // find their audio already buffered — but they do not publish TtsLatencyMs, so the
        // decomposition is unaffected.
        var job = new PlaybackJob(
            Label: $"{(isReply ? "reply" : "preamble")}:{session.SatelliteId}",
            Kind: isReply ? PlaybackKind.Reply : PlaybackKind.Preamble,
            Priority: AnnouncePriority.Normal,
            Audio: tts.SynthesizeAsync(text, options, default),
            EnqueuedAt: enqueuedAt,
            // Anchored to the turn's FIRST reply segment — never the preamble flush, which runs
            // with isReply: false and publishes nothing. Under streaming that segment is the first
            // flushable sentence the model produced: normally the answer's opening, but a model
            // that narrates a complete sentence before its first tool call anchors here too. That
            // is deliberate — these spans measure how long the user waited to hear a reply, and
            // narration the user hears IS the reply starting; anchoring at the later answer would
            // also leave the queue-wait and TTS spans measured against a different job than the
            // turn spans, and the decomposition would stop summing.
            OnFirstAudio: timing =>
            {
                if (!segment.IsFirst)
                {
                    return Task.CompletedTask;
                }

                metrics.Publish(new VoiceEvent
                {
                    Metric = VoiceMetric.TtsLatencyMs,
                    DurationMs = (long)timing.SinceSynthesisStart.TotalMilliseconds,
                    ConversationId = conversationId
                }.About(session));

                // Anchored on this job alone (its own enqueue stamp), so it is valid for every reply
                // regardless of what preceded it.
                if (timing.QueueWait is { } queueWait)
                {
                    metrics.Publish(new VoiceEvent
                    {
                        Metric = VoiceMetric.ReplyQueueWaitMs,
                        DurationMs = (long)queueWait.TotalMilliseconds,
                        ConversationId = conversationId
                    }.About(session));
                }

                // The two below are anchored on the TURN (MarkTurnStart / MarkSpeechEnd), which is
                // never invalidated, while the voice conversation mapping outlives the turn by
                // ConversationLifetime. A schedule fire or an agent-initiated message delivered into a
                // live session comes down this same path, so without a gate it reports the AGE of the
                // last real turn as its own latency — one "recuérdame en dos minutos" publishes
                // ~120000 ms and wrecks Avg/P95/Max on the headline metric. The consumed dispatch
                // stamp is the proof that what is being answered is a transcript this hub dispatched.
                if (dispatchedAtStamp is null)
                {
                    return Task.CompletedTask;
                }

                if (timing.SinceTurnStart is { } turn)
                {
                    metrics.Publish(new VoiceEvent
                    {
                        Metric = VoiceMetric.WakeToFirstAudioMs,
                        DurationMs = (long)turn.TotalMilliseconds,
                        ConversationId = conversationId
                    }.About(session));
                }

                if (timing.SinceSpeechEnd is { } sinceSpeech)
                {
                    metrics.Publish(new VoiceEvent
                    {
                        Metric = VoiceMetric.SpeechEndToFirstAudioMs,
                        DurationMs = (long)sinceSpeech.TotalMilliseconds,
                        ConversationId = conversationId
                    }.About(session));
                }

                return Task.CompletedTask;
            });

        // Counted before the enqueue so the job cannot drain against a segment that was never
        // registered. A refused enqueue (queue full, or the satellite disconnected and completed the
        // writer) settles the segment immediately — otherwise the handshake would wait out the
        // ~120s ReplyTimeoutMs for audio that will never play.
        if (isReply)
        {
            segment = session.Turn.BeginSegment();
        }

        var ticket = session.Playback.Enqueue(job);
        if (isReply && ticket.Refused is { } reason)
        {
            logger.LogWarning(
                "Reply segment for {Satellite} was refused by the playback queue ({Reason}); " +
                "this part of the answer will not be spoken", session.SatelliteId, reason);
        }

        if (!isReply)
        {
            return ticket.Refused;
        }

        // One binding for every way the job can end, where there used to be a release on three
        // separate paths and a disposal on the same three. A missed release left the turn
        // outstanding until the ~120 s reply timeout, with the microphone shut for all of it.
        _ = ticket.Completed.ContinueWith(
            settled => SettleSegment(settled.Result, segment, session, conversationId),
            TaskScheduler.Default);

        return ticket.Refused;
    }

    // Runs unobserved on the queue's signal, so it guards itself: the queue no longer swallows what
    // a producer throws, and a segment left outstanding is exactly the wedged microphone above.
    private void SettleSegment(
        PlaybackOutcome outcome,
        SegmentToken segment,
        SatelliteSession session,
        string conversationId)
    {
        try
        {
            // A drain resolves this SEGMENT, not the turn: the turn settles once every started
            // segment has drained and the agent's stream has ended. Every other ending fails the
            // segment — the turn still settles Spoken when earlier audio reached the satellite,
            // which is what an alarm cutting into a reply looks like, and Silent when none did.
            if (outcome.Kind == PlaybackOutcomeKind.Drained)
            {
                segment.Complete();
                return;
            }

            segment.Fail();

            // One sample per turn, like the other reply-anchored metrics.
            if (outcome.Kind != PlaybackOutcomeKind.Preempted || !segment.IsFirst)
            {
                return;
            }

            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.AnnouncePreemptedReply,
                ConversationId = conversationId
            }.About(session));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Settling the reply segment for {Satellite} failed", session.SatelliteId);
        }
    }
}