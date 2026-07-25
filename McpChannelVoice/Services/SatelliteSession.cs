using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed record PlaybackJob(
    string Label,
    AnnouncePriority Priority,
    IAsyncEnumerable<AudioChunk> Audio,
    Func<string, Task> OnStarted,
    Func<string, Task> OnPreempted,
    Func<Task>? OnDrained = null,
    Func<FirstAudioTiming, Task>? OnFirstAudio = null,
    Func<Exception, Task>? OnFailed = null,
    long EnqueuedAt = 0);

// Timing captured the moment a job's first audio chunk is produced. SinceSynthesisStart is the
// TTS time-to-first-audio (synthesis request -> first chunk); SinceTurnStart is the wake/turn-open
// -> first audio latency, null when the job had no preceding user turn. SinceSpeechEnd is the same
// span measured from the instant the user stopped talking (see MarkSpeechEnd: capture close rewound
// by the endpointing tail) — the machine time the user actually waits through, with their own speech
// excluded and the endpointing tail included. QueueWait ends at synthesis start, so it never
// overlaps SinceSynthesisStart.
public sealed record FirstAudioTiming(
    TimeSpan SinceSynthesisStart,
    TimeSpan? SinceTurnStart,
    TimeSpan? SinceSpeechEnd = null,
    TimeSpan? QueueWait = null);

public sealed class SatelliteSession
{
    private readonly Channel<(long Seq, PlaybackJob Job)> _playback =
        Channel.CreateUnbounded<(long Seq, PlaybackJob Job)>();
    private CancellationTokenSource? _currentPlaybackCts;
    private readonly Lock _gate = new();
    private long _enqueueSeq;
    // High-water sequence whose jobs must be preempted as they start. Set only when a high-priority
    // job arrives while no job is marked current (the gap between dequeue and assignment, or idle),
    // so a preemption can't be lost to that race. High-priority jobs are exempt from this mark (see
    // the loop), so a second High job stacking in the same window never preempts the first.
    private long _preemptPendingSeq = -1;
    private UtteranceCapture? _capture;
    private readonly Lock _turnGate = new();
    private TaskCompletionSource<bool> _turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const long TurnNotStarted = long.MinValue;
    private long _turnStartedAt = TurnNotStarted;
    private const long SpeechEndNotMarked = long.MinValue;
    private long _speechEndedAt = SpeechEndNotMarked;
    private const long DispatchNotMarked = long.MinValue;
    private long _dispatchedAt = DispatchNotMarked;
    private int _preambleClaimed;
    private int _replySegmentsStarted;
    private int _replySegmentsOutstanding;
    private int _replyStreamComplete;
    private int _replyAudioPlayed;
    private long _turnEpoch;
    private static readonly TimeSpan _snoozeWindow = TimeSpan.FromSeconds(60);
    private readonly Lock _dismissGate = new();
    private string? _dismissedAlert;
    private DateTimeOffset _dismissedAt;

    public SatelliteSession(string satelliteId, SatelliteConfig config)
    {
        SatelliteId = satelliteId;
        Config = config;
    }

    public string SatelliteId { get; }
    public SatelliteConfig Config { get; }

    public ValueTask<bool> EnqueuePlaybackAsync(PlaybackJob job, int queueMaxDepth)
    {
        if (job.Priority == AnnouncePriority.High)
        {
            long seq;
            lock (_gate)
            {
                // Cancel the in-flight job if one is marked current; otherwise record a preempt
                // high-water mark the loop honors when it next assigns a job, closing the race
                // where _currentPlaybackCts is momentarily null during the dequeue->assign gap.
                if (_currentPlaybackCts is not null)
                {
                    _currentPlaybackCts.Cancel();
                }
                else
                {
                    _preemptPendingSeq = _enqueueSeq;
                }
                seq = ++_enqueueSeq;
            }
            // TryWrite (unbounded channel) returns false only once the writer is completed — i.e. the
            // satellite disconnected and the playback loop tore down. Returning false instead of
            // throwing ChannelClosedException lets callers (e.g. announce) record a dropped target.
            return ValueTask.FromResult(_playback.Writer.TryWrite((seq, job)));
        }

        if (job.Priority == AnnouncePriority.Low && _playback.Reader.Count > 0)
        {
            return ValueTask.FromResult(false);
        }

        if (_playback.Reader.Count >= queueMaxDepth)
        {
            return ValueTask.FromResult(false);
        }

        long normalSeq;
        lock (_gate)
        {
            normalSeq = ++_enqueueSeq;
        }
        return ValueTask.FromResult(_playback.Writer.TryWrite((normalSeq, job)));
    }

    public void CompletePlayback() => _playback.Writer.TryComplete();

    public void PreemptCurrent()
    {
        lock (_gate)
        {
            _currentPlaybackCts?.Cancel();
        }
    }

    public UtteranceCapture OpenCapture(SilenceGate gate)
    {
        var capture = new UtteranceCapture(gate);
        Volatile.Write(ref _capture, capture);
        return capture;
    }

    public void CloseCapture() => Volatile.Write(ref _capture, null);

    public bool HasActiveCapture => Volatile.Read(ref _capture) is not null;

    public void RouteAudio(AudioChunk chunk) => Volatile.Read(ref _capture)?.Feed(chunk);

    public void EndCapture() => Volatile.Read(ref _capture)?.ForceEnd();

    // Records the timestamp (from the playback loop's TimeProvider) at which the current user turn
    // began, so the loop can report wake/turn -> first-audio latency. Set at capture-open each turn.
    public void MarkTurnStart(long timestamp) => Interlocked.Exchange(ref _turnStartedAt, timestamp);

    // Records when the user stopped talking — everything after this is machine time they wait
    // through. The caller can only observe the CLOSE of the capture, which is a whole endpointing
    // tail later: SilenceGate only concludes "speech ended" once trailingSilence (1.2 s in production)
    // of silence has run. Rewinding by that frozen tail is what makes this the instant the user
    // actually stopped, so EndpointTailMs nests INSIDE SpeechEndToFirstAudioMs instead of sitting
    // before it and the turn decomposition sums. Legitimate because mic audio arrives in real time,
    // so the tail's audio-domain length is also its wall-clock length; the only residual error is
    // the gate-decision -> capture-close handoff. Stamped with the same TimeProvider the playback
    // loop reads, exactly like MarkTurnStart, so the two spans are comparable.
    public void MarkSpeechEnd(long captureClosedAt, long endpointTailMs, TimeProvider time) =>
        Interlocked.Exchange(
            ref _speechEndedAt, captureClosedAt - (endpointTailMs * time.TimestampFrequency / 1000));

    // Stamped when a transcript actually reached the agent, so the hub can measure the agent round
    // trip it cannot otherwise see into (the agent's own MemoryRecall/LlmTotal stages live in a
    // different process). Single-use, like NoteDismissedAlert/TryConsumeDismissedAlert below: a live
    // session's conversation can also receive a schedule-fired or agent-initiated reply that never
    // went through a transcript dispatch, so a stamp left over from an earlier real turn must not be
    // readable by that later, unrelated reply — it would report an invented, stale round trip.
    public void MarkDispatched(long timestamp) => Interlocked.Exchange(ref _dispatchedAt, timestamp);

    public long? TryConsumeDispatchedAt()
    {
        var stamp = Interlocked.Exchange(ref _dispatchedAt, DispatchNotMarked);
        return stamp == DispatchNotMarked ? null : stamp;
    }

    // Callers must ResetTurn before the reply path can SignalTurnSpoken/SignalTurnSilent for
    // the new turn; otherwise a signal lands on the discarded TCS and the awaiter blocks forever.
    public void ResetTurn()
    {
        Interlocked.Exchange(ref _preambleClaimed, 0);
        Interlocked.Exchange(ref _replySegmentsStarted, 0);
        Interlocked.Exchange(ref _replySegmentsOutstanding, 0);
        Interlocked.Exchange(ref _replyStreamComplete, 0);
        Interlocked.Exchange(ref _replyAudioPlayed, 0);
        Interlocked.Increment(ref _turnEpoch);
        lock (_turnGate)
        {
            _turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // The reply is streamed as several sentence jobs, so "the answer finished" is no longer "a job
    // drained" — it is "every started segment drained AND the agent's stream ended". Signalling on
    // the first drain instead would end FollowUpConversation, which chimes and reopens the mic while
    // the remaining sentences are still being spoken.
    public int ReplySegmentsStarted => Volatile.Read(ref _replySegmentsStarted);

    // Playback callbacks outlive the turn that queued them (a preempted or slow job can drain after
    // FollowUpConversation has moved on), and this handshake is now counter-based rather than the
    // old idempotent "set the TCS" — so a stale decrement would drive the NEXT turn's outstanding
    // count negative and it could then never reach zero, wedging the mic until ReplyTimeoutMs.
    // Callbacks carry the epoch they were queued under and are ignored once it moves.
    public long CurrentTurnEpoch => Interlocked.Read(ref _turnEpoch);

    public void BeginReplySegment()
    {
        Interlocked.Increment(ref _replySegmentsStarted);
        Interlocked.Increment(ref _replySegmentsOutstanding);
    }

    public void CompleteReplySegment(long epoch)
    {
        if (epoch != CurrentTurnEpoch)
        {
            return;
        }
        Interlocked.Exchange(ref _replyAudioPlayed, 1);
        Interlocked.Decrement(ref _replySegmentsOutstanding);
        SettleIfComplete();
    }

    // A segment that never plays (synthesis threw, or the queue refused it) must NOT settle the turn
    // on its own: sentences behind it may still be queued, and settling here would end
    // FollowUpConversation, whose chime is a High-priority job — it would preempt the sentence
    // currently playing and the rest would then be spoken into an open capture.
    public void FailReplySegment(long epoch)
    {
        if (epoch != CurrentTurnEpoch)
        {
            return;
        }
        Interlocked.Decrement(ref _replySegmentsOutstanding);
        SettleIfComplete();
    }

    public void MarkReplyStreamComplete()
    {
        Interlocked.Exchange(ref _replyStreamComplete, 1);
        SettleIfComplete();
    }

    // The turn is over only once the agent has stopped sending AND every segment it produced has
    // finished. Spoken when any segment reached the satellite — half an answer still played, and the
    // user is owed the follow-up window; Silent when every one of them failed. An empty answer starts
    // no segments and is left for the caller to settle explicitly.
    private void SettleIfComplete()
    {
        if (Volatile.Read(ref _replyStreamComplete) != 1
            || Volatile.Read(ref _replySegmentsOutstanding) != 0
            || ReplySegmentsStarted == 0)
        {
            return;
        }

        if (Volatile.Read(ref _replyAudioPlayed) == 1)
        {
            SignalTurnSpoken();
        }
        else
        {
            SignalTurnSilent();
        }
    }

    // Claimed by the first tool call of a turn, which speaks whatever the model said before it
    // ("Buscando") instead of leaving it buffered until the answer. One claim per turn: later tool
    // calls keep mid-run narration buffered so it cannot race the answer into the playback queue.
    public bool TryClaimPreamble() => Interlocked.CompareExchange(ref _preambleClaimed, 1, 0) == 0;

    public Task<bool> WaitForTurnSpokenAsync()
    {
        lock (_turnGate)
        {
            return _turn.Task;
        }
    }

    public void SignalTurnSpoken()
    {
        lock (_turnGate)
        {
            _turn.TrySetResult(true);
        }
    }

    public void SignalTurnSilent()
    {
        lock (_turnGate)
        {
            _turn.TrySetResult(false);
        }
    }

    // Wake-word dismissal context for LLM-mediated snooze: the host stashes what was dismissed; the
    // next dispatched transcript within the window consumes it (single-use).
    public void NoteDismissedAlert(string description, DateTimeOffset now)
    {
        lock (_dismissGate)
        {
            _dismissedAlert = description;
            _dismissedAt = now;
        }
    }

    public string? TryConsumeDismissedAlert(DateTimeOffset now)
    {
        lock (_dismissGate)
        {
            var value = _dismissedAlert is not null && now - _dismissedAt <= _snoozeWindow
                ? _dismissedAlert
                : null;
            _dismissedAlert = null;
            return value;
        }
    }

    public async Task RunPlaybackLoopAsync(
        Func<AudioChunk, CancellationToken, Task> writer,
        CancellationToken ct,
        TimeProvider? time = null,
        ILogger? logger = null,
        Func<AudioFormat, CancellationToken, Task>? onAudioStart = null,
        Func<CancellationToken, Task>? onAudioStop = null,
        Func<PlaybackJob, Exception, Task>? onError = null)
    {
        time ??= TimeProvider.System;
        await foreach (var (seq, job) in _playback.Reader.ReadAllAsync(ct))
        {
            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bool preemptOnStart;
            lock (_gate)
            {
                _currentPlaybackCts = jobCts;
                // Exempt High jobs: the mark exists to drop lower-priority jobs queued ahead of a
                // High request; a second High stacking in the gap must still play, not be preempted.
                preemptOnStart = _preemptPendingSeq >= 0 && seq <= _preemptPendingSeq
                    && job.Priority != AnnouncePriority.High;
                _preemptPendingSeq = -1;
            }
            if (preemptOnStart)
            {
                jobCts.Cancel();
            }

            var chunks = 0;
            var drained = false;
            long firstChunkTimestamp = 0;
            var totalAudio = TimeSpan.Zero;
            try
            {
                // OnStarted side effects (e.g. a metrics publish) must neither abort this job's
                // playback nor tear down the loop, so swallow their failures here. Keeping it inside
                // the try also guarantees the finally cleanup runs no matter what.
                try
                {
                    await job.OnStarted(job.Label);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Playback OnStarted callback failed for {Label}", job.Label);
                }

                // Synthesis is lazy (the TTS enumerable is pulled here), so time it from just before
                // the first pull to the first chunk — not from enqueue, which is a near-zero channel write.
                var synthesisStart = time.GetTimestamp();
                await foreach (var chunk in job.Audio.WithCancellation(jobCts.Token))
                {
                    FirstAudioTiming? firstAudio = null;
                    if (chunks == 0)
                    {
                        firstChunkTimestamp = time.GetTimestamp();
                        if (onAudioStart is not null)
                        {
                            await onAudioStart(chunk.Format, jobCts.Token);
                        }
                        if (job.OnFirstAudio is not null)
                        {
                            var turnStart = Interlocked.Read(ref _turnStartedAt);
                            var speechEnd = Interlocked.Read(ref _speechEndedAt);
                            firstAudio = new FirstAudioTiming(
                                time.GetElapsedTime(synthesisStart, firstChunkTimestamp),
                                turnStart == TurnNotStarted
                                    ? null
                                    : time.GetElapsedTime(turnStart, firstChunkTimestamp),
                                speechEnd == SpeechEndNotMarked
                                    ? null
                                    : time.GetElapsedTime(speechEnd, firstChunkTimestamp),
                                job.EnqueuedAt == 0
                                    ? null
                                    : time.GetElapsedTime(job.EnqueuedAt, synthesisStart));
                        }
                    }
                    totalAudio += DurationOf(chunk);
                    chunks++;
                    await writer(chunk, jobCts.Token);
                    // Deliberately after the write: OnFirstAudio carries several awaited metric
                    // publishes, and running them first would delay the first audio byte reaching the
                    // satellite by however long the metrics backbone takes — the observer changing what
                    // it observes. Every timestamp above is already captured, so accuracy is unaffected.
                    // A failing metrics publish must neither abort playback nor tear down the loop.
                    if (firstAudio is { } timing)
                    {
                        try
                        {
                            await job.OnFirstAudio!(timing);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Playback OnFirstAudio callback failed for {Label}", job.Label);
                        }
                    }
                }
                logger?.LogInformation("Playback job {Label} drained {Chunks} chunk(s)", job.Label, chunks);
                drained = true;
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try
                {
                    await job.OnPreempted(job.Label);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Playback OnPreempted callback failed for {Label}", job.Label);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One job's audio failing (TTS synthesis or a transient write) must not tear down the
                // whole playback loop: log it, surface it via onError, and continue to the next job.
                logger?.LogWarning(ex, "Playback job {Label} failed after {Chunks} chunk(s)", job.Label, chunks);
                if (onError is not null)
                {
                    try
                    {
                        await onError(job, ex);
                    }
                    catch (Exception oex)
                    {
                        logger?.LogWarning(oex, "Playback onError handler threw for {Label}", job.Label);
                    }
                }
                // Signal terminal completion to anyone awaiting this job (e.g. an approval prompt or
                // chime blocked on its drained handshake), so a synthesis failure doesn't hang them.
                if (job.OnFailed is not null)
                {
                    try
                    {
                        await job.OnFailed(ex);
                    }
                    catch (Exception fex)
                    {
                        logger?.LogWarning(fex, "Playback OnFailed callback failed for {Label}", job.Label);
                    }
                }
            }
            finally
            {
                // Close the playback envelope so the satellite flushes paplay (EOF on
                // disconnect_after_stop). Use the connection token: jobCts may be canceled
                // by preemption, but the satellite still needs the audio-stop. A bare
                // audio-start with no chunks gets no stop, matching Wyoming framing.
                if (chunks > 0 && onAudioStop is not null && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await onAudioStop(ct);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to send audio-stop for {Label}", job.Label);
                    }
                }
                if (drained && totalAudio > TimeSpan.Zero && !ct.IsCancellationRequested)
                {
                    // OnDrained means "the satellite finished PLAYING", not "we finished writing".
                    // The Pi buffers the audio and plays PCM at real time, so wait out the remaining
                    // nominal duration. Self-corrects for back-pressuring satellites (remaining <= 0).
                    var remaining = totalAudio - time.GetElapsedTime(firstChunkTimestamp);
                    if (remaining > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(remaining, time, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            // Connection tearing down.
                        }
                    }
                }
                if (drained && job.OnDrained is not null && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await job.OnDrained();
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Playback OnDrained callback failed for {Label}", job.Label);
                    }
                }
                lock (_gate)
                { _currentPlaybackCts = null; }
                jobCts.Dispose();
            }
        }
    }

    private static TimeSpan DurationOf(AudioChunk chunk)
    {
        var format = chunk.Format;
        var bytesPerSecond = format.SampleRateHz * format.SampleWidthBytes * format.Channels;
        return bytesPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)chunk.Data.Length / bytesPerSecond);
    }
}