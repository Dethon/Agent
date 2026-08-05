using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// What a producer is queueing. One fact, from which the queue derives how much of that kind it will
// hold at once and whether the audio takes the satellite's alert route — three conventions (a label
// prefix, a depth limit each producer read from settings, a boolean any producer could set) that
// were really this one thing spelled three ways.
public enum PlaybackKind
{
    Reply,
    Preamble,
    Announce,
    Alarm,
    Chime,
    Approval
}

// Label stays free text for logs; Kind is what the queue reads. How a job ENDS is the ticket's
// outcome, never a callback here — the three terminal callbacks this used to carry were mutually
// exclusive with nothing in the type saying so, and each of six producers worked the rule out again.
// OnFirstAudio survives because it is the one genuinely non-terminal observation: it reports timing
// from between two audio writes and cannot end anything.
public sealed record PlaybackJob(
    string Label,
    PlaybackKind Kind,
    AnnouncePriority Priority,
    IAsyncEnumerable<AudioChunk> Audio,
    Func<FirstAudioTiming, Task>? OnFirstAudio = null,
    long EnqueuedAt = 0);

// The wire side of the loop, as one collaborator: where chunks go, the audio envelope around
// them, and the per-job error report. These are the connection's hooks and the only callbacks
// the loop awaits (a producer's reaction to an outcome runs on its own, off the ticket).
public sealed record PlaybackSink(
    Func<AudioChunk, CancellationToken, Task> Writer,
    Func<AudioFormat, bool, CancellationToken, Task>? OnAudioStart = null,
    Func<CancellationToken, Task>? OnAudioStop = null,
    Func<PlaybackJob, Exception, Task>? OnError = null);

// What queueing a job hands back. Refused says immediately whether the queue turned the job away and
// why; Completed is the one outcome that will end it. A refused ticket's Completed is already
// settled, so a caller has one settle path rather than a branch.
public readonly record struct PlaybackTicket(RefusalReason? Refused, Task<PlaybackOutcome> Completed);

// How a job ended. Exactly one of these per job, always — including a job the queue turned away and
// including every job still waiting when the satellite disappears. Error is set for Failed only.
public sealed record PlaybackOutcome(PlaybackOutcomeKind Kind, int ChunksWritten = 0, Exception? Error = null);

public enum PlaybackOutcomeKind
{
    // Heard to the end: the audio was written and its real-time playback waited out.
    Drained,
    // Cut short by a high-priority job or an alert dismissal.
    Preempted,
    // The audio source threw — a synthesis error, or a write that failed.
    Failed,
    // Never queued. See RefusalReason.
    Refused,
    // The connection died before the job could be heard.
    Discarded
}

public enum RefusalReason
{
    // The satellite disconnected and the queue was completed.
    QueueClosed,
    // This kind's allowance is already full.
    QueueFull,
    // A low-priority job arrived while something was already queued, so it would delay speech.
    LowPriorityBehindQueue
}

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

// Where a user turn begins and where the user stopped talking, as the playback loop needs them to
// report its latencies. The queue is what reads them back, but every producer holds the queue and a
// caller that is not a turn — the approval prompt's one-shot listen, say — would be one call away
// from re-anchoring the turn actually in flight. So the anchors are not on the queue's surface: they
// are implemented explicitly, and a caller has to hold this interface to write one. CaptureSession
// is the type that does, which is ADR 0013's "which type a caller holds is the statement that it is
// or is not a turn" made true rather than merely observed.
public interface ITurnAnchor
{
    void MarkTurnStart(long timestamp);

    void MarkSpeechEnd(long captureClosedAt, long endpointTailMs, TimeProvider time);
}

// What a satellite hears next: everything queued for playback on one connection, played one job at a
// time in the order it was accepted, with a high-priority job cutting in. One queue per satellite
// connection — built with the connection, whose run drives its loop and whose drain settles what was
// never played; the satellite session exposes it as a property so producers reach it without a
// pass-through layer.
// The two depth limits are the queue's, not a producer's: an answer's segments get their own
// allowance (sharing the announce depth meant one turn's answer competed with itself and lost
// sentences out of its middle), everything else shares the announce one. A null prefetch size means
// prefetching is switched off, and a segment's synthesis then starts when the loop reaches it. The
// defaults are the settings' own, so a queue built without configuration behaves as configuration
// would have made it.
public sealed class PlaybackQueue(
    int replyMaxDepth = StreamingTtsConfig.DefaultMaxQueuedSegments,
    int announceMaxDepth = AnnounceSettings.DefaultQueueMaxDepth,
    int? prefetchBufferChunks = StreamingTtsConfig.DefaultPrefetchBufferChunks) : ITurnAnchor, IDisposable
{
    // Play order, guarded by _gate: normal jobs append, a High job cuts in ahead of the queued
    // normal jobs (behind any High already waiting, so Highs stay FIFO among themselves). A channel
    // could not express the cut-in, so the pending list is its own structure and the semaphore is
    // the loop's wake-up.
    private readonly List<QueuedJob> _pending = [];
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _currentCts;
    // The job the loop is playing, kept so the drain can settle it when the connection dies before
    // the loop could reach any terminal path of its own.
    private QueuedJob? _inFlight;
    private bool _closed;
    // Set by the link-drop close: the loop discards every job it dequeues from then on instead of
    // playing it, because there is no satellite left to hear it.
    private bool _discardOnDequeue;
    private readonly Lock _gate = new();
    // Cancelled by the link-drop close, and only by it, so a drain never sits out an in-flight
    // job's real-time tail against a dead socket. Playback on a live link never observes it.
    private readonly CancellationTokenSource _dropCts = new();
    private long _enqueueSeq;
    // High-water sequence whose jobs must be preempted as they start. Set only by an ALARM, so a
    // preemption can't be lost to the dequeue->assign gap. High-priority jobs are exempt from this
    // mark (see the loop), so a second High job stacking in the same window never preempts the first.
    private long _preemptPendingSeq = -1;
    private const long TurnNotStarted = long.MinValue;
    private long _turnStartedAt = TurnNotStarted;
    private const long SpeechEndNotMarked = long.MinValue;
    private long _speechEndedAt = SpeechEndNotMarked;

    private int Depth
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    // Lets a caller decide whether a job of this kind can be queued BEFORE it consumes the text and
    // starts its synthesis, rather than finding out after both are already spent. It answers for the
    // kind, not for a particular job: the limit is the only thing it can know in advance.
    public bool CanAccept(PlaybackKind kind) => Depth < MaxDepthFor(kind);

    // Only an answer's segments get the reply allowance. The preamble cue is one job that plays
    // ahead of an answer, not part of it, so it shares the announce depth as it always did.
    private int MaxDepthFor(PlaybackKind kind) =>
        kind == PlaybackKind.Reply ? replyMaxDepth : announceMaxDepth;

    // Synchronous: every branch below answers immediately, so a ticket rather than a task. One
    // awaitable could not answer "was this accepted" now and "how did it end" later, and two
    // producers need the first answer immediately — announce to fill its per-target status, the reply
    // tool to release a segment it has just registered.
    public PlaybackTicket Enqueue(PlaybackJob job)
    {
        if (job.Priority == AnnouncePriority.High)
        {
            lock (_gate)
            {
                if (job.Kind == PlaybackKind.Alarm)
                {
                    // Mark EVERY job queued so far, then cancel the in-flight one. Cancelling only
                    // the current job was enough when a reply was a single job; now that it is
                    // several sentence jobs, an alarm cut sentence one and was then heard only
                    // after sentences 2..N had played in full — and a queued alarm the user had
                    // already acknowledged still rang, because dismissal preempts the current job.
                    // The mark is taken before this job's seq is issued, so its own seq exceeds it
                    // and the loop's High exemption keeps a stacked second High playing. It also
                    // closes the original race, where _currentCts is momentarily null during the
                    // dequeue->assign gap. The flush is the alarm's alone: an approval prompt or a
                    // chime cuts in ahead of the queued jobs instead, and they play after it — a
                    // prompt that flushed a streamed answer swallowed its remaining sentences.
                    _preemptPendingSeq = _enqueueSeq;
                }
                _currentCts?.Cancel();
                return WriteLocked(job, cutAheadOfQueued: job.Kind != PlaybackKind.Alarm);
            }
        }

        // Decided and inserted under one lock hold: read outside it, two producers racing for the
        // last slot both see room and both insert, and a kind holds one more job than its allowance
        // says it ever will. CanAccept above stays an advance question a caller may ask; this is the
        // answer that binds.
        lock (_gate)
        {
            if (job.Priority == AnnouncePriority.Low && _pending.Count > 0)
            {
                return Refuse(RefusalReason.LowPriorityBehindQueue);
            }

            if (_pending.Count >= MaxDepthFor(job.Kind))
            {
                return Refuse(RefusalReason.QueueFull);
            }

            return WriteLocked(job, cutAheadOfQueued: false);
        }
    }

    // A completed queue means the satellite disconnected and the loop tore down. Checked before the
    // job is accepted rather than after, so the prefetch below is only ever created for a job that
    // was taken: a refused job has nothing to dispose, and the trickiest disposal path stops
    // existing instead of being handled. The closed check and the insert share one lock hold, so
    // acceptance cannot race Complete().
    private PlaybackTicket WriteLocked(PlaybackJob job, bool cutAheadOfQueued)
    {
        if (_closed)
        {
            return Refuse(RefusalReason.QueueClosed);
        }

        // The queue owns the audio source's lifetime from here: it starts a reply segment's
        // synthesis early — the loop will not touch this job's audio until the previous segment has
        // finished its real-time drain, which would put a full TTS round trip into every sentence
        // seam — and disposes it once the job has settled.
        var seq = ++_enqueueSeq;
        var prefetch = job.Kind == PlaybackKind.Reply && prefetchBufferChunks is { } capacity
            ? new PrefetchedAudio(job.Audio, capacity)
            : null;
        var queued = new QueuedJob(
            seq, prefetch is null ? job : job with { Audio = prefetch.Chunks }, prefetch);

        // A cut-in lands behind the Highs already waiting and ahead of everything else; an alarm
        // appends instead, so the jobs its mark preempts drain (and settle) before it rings.
        var at = cutAheadOfQueued
            ? _pending.FindLastIndex(p => p.Job.Priority == AnnouncePriority.High) + 1
            : _pending.Count;
        _pending.Insert(at, queued);
        _signal.Release();
        return new PlaybackTicket(null, queued.Completed);
    }

    // A refused job has an outcome like any other, already settled, so nobody writes a second settle
    // path for it — and nothing was ever created for it, so there is nothing to dispose either.
    private static PlaybackTicket Refuse(RefusalReason reason) => new(
        reason, Task.FromResult(new PlaybackOutcome(PlaybackOutcomeKind.Refused)));

    // Called by the connection's drain, the one place that knows the link is gone for good: the job
    // the loop was playing and everything still queued behind it end as discarded. Settling is
    // first-wins, so a job the loop already finished keeps the outcome it earned.
    public void DiscardUnplayed()
    {
        QueuedJob? inFlight;
        List<QueuedJob> unplayed;
        lock (_gate)
        {
            inFlight = _inFlight;
            unplayed = [.. _pending];
            _pending.Clear();
        }
        if (inFlight is not null)
        {
            inFlight.Settle(PlaybackOutcomeKind.Discarded);
            _ = inFlight.ReleaseAudioAsync();
        }

        foreach (var queued in unplayed)
        {
            queued.Settle(PlaybackOutcomeKind.Discarded);
            _ = queued.ReleaseAudioAsync();
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _closed = true;
        }
        _signal.Release();
    }

    // The link-drop close. Complete() alone is not enough there: the run token is still live, so
    // the loop would otherwise be handed every queued job to synthesize and write into the dead
    // socket — each settling Failed with an error report the producer never earned. Marking the
    // discard first makes the loop settle those jobs Discarded as it dequeues them, while the job
    // being played is left to end as it really did — except its real-time tail, which is cut: the
    // drain awaits the loop, and a fully-written job's tail is a delay nobody is listening to that
    // would stall reconnect by the remaining audio duration. Its audio was already written, so it
    // still settles Drained. Shutdown needs none of this: cancelling the run token stops the loop
    // outright and DiscardUnplayed sweeps what it left behind.
    public void CompleteAndDiscardQueued()
    {
        Volatile.Write(ref _discardOnDequeue, true);
        Complete();
        _dropCts.Cancel();
    }

    // The semaphore and the drop token source are the queue's own, and there is one queue per
    // satellite connection: undisposed they are a pair leaked on every reconnect. Disposal is not a
    // close — Complete() is — so it belongs after the loop has stopped, which is the only place that
    // knows nothing will wait on the semaphore again (SatelliteConnection's drain). A producer
    // arriving later still gets the closed queue's refusal, because that answer is given before
    // anything touches the signal.
    public void Dispose()
    {
        _signal.Dispose();
        _dropCts.Dispose();
    }

    public void PreemptCurrent()
    {
        lock (_gate)
        {
            _currentCts?.Cancel();
        }
    }

    // Records the timestamp (from the loop's TimeProvider) at which the current user turn began, so
    // the loop can report wake/turn -> first-audio latency. Set at capture-open each turn.
    void ITurnAnchor.MarkTurnStart(long timestamp) => Interlocked.Exchange(ref _turnStartedAt, timestamp);

    // Records when the user stopped talking — everything after this is machine time they wait
    // through. The caller can only observe the CLOSE of the capture, which is a whole endpointing
    // tail later: SilenceGate only concludes "speech ended" once trailingSilence (1.2 s in production)
    // of silence has run. Rewinding by that frozen tail is what makes this the instant the user
    // actually stopped, so EndpointTailMs nests INSIDE SpeechEndToFirstAudioMs instead of sitting
    // before it and the turn decomposition sums. Legitimate because mic audio arrives in real time,
    // so the tail's audio-domain length is also its wall-clock length; the only residual error is
    // the gate-decision -> capture-close handoff. Stamped with the same TimeProvider the loop reads,
    // exactly like MarkTurnStart, so the two spans are comparable.
    void ITurnAnchor.MarkSpeechEnd(long captureClosedAt, long endpointTailMs, TimeProvider time) =>
        Interlocked.Exchange(
            ref _speechEndedAt, captureClosedAt - (endpointTailMs * time.TimestampFrequency / 1000));

    // Most callers only have chunks to write; the connection passes a full sink.
    public Task RunAsync(
        Func<AudioChunk, CancellationToken, Task> writer,
        CancellationToken ct,
        TimeProvider? time = null,
        ILogger? logger = null) =>
        RunAsync(new PlaybackSink(writer), ct, time, logger);

    public async Task RunAsync(
        PlaybackSink sink,
        CancellationToken ct,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        time ??= TimeProvider.System;
        while (await TakeNextAsync(ct) is { } queued)
        {
            if (Volatile.Read(ref _discardOnDequeue))
            {
                queued.Settle(PlaybackOutcomeKind.Discarded);
                await queued.ReleaseAudioAsync();
                continue;
            }

            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (TakeAsCurrent(queued, jobCts))
            {
                jobCts.Cancel();
            }

            await PlayAsync(queued, jobCts, sink, time, logger, ct);
        }
    }

    // Null means the queue closed and nothing is left to play. The semaphore may wake the loop more
    // often than there are jobs (each Complete releases it once too), so an empty wake just parks
    // again — the pending list under the gate is the truth, the semaphore only a wake-up.
    private async Task<QueuedJob?> TakeNextAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_pending.Count > 0)
                {
                    var queued = _pending[0];
                    _pending.RemoveAt(0);
                    return queued;
                }
                if (_closed)
                {
                    return null;
                }
            }
            await _signal.WaitAsync(ct);
        }
    }

    // Marks the job current, and answers whether it must be preempted before its first chunk.
    private bool TakeAsCurrent(QueuedJob queued, CancellationTokenSource jobCts)
    {
        lock (_gate)
        {
            _currentCts = jobCts;
            _inFlight = queued;
            // Exempt High jobs: the mark exists to drop lower-priority jobs queued ahead of a
            // High request; a second High stacking in the gap must still play, not be preempted.
            var preemptOnStart = _preemptPendingSeq >= 0 && queued.Seq <= _preemptPendingSeq
                && queued.Job.Priority != AnnouncePriority.High;
            // Cleared once nothing at or below the mark is left queued, so every job that was
            // already there when the alarm arrived is preempted — not just the first one dequeued
            // after it. Asking the pending list rather than this job's sequence keeps that true
            // whatever order jobs are inserted in: a High cut-in carries a sequence above the mark,
            // and clearing on that alone would spare whatever the alarm marked behind it.
            if (!_pending.Any(p => p.Seq <= _preemptPendingSeq))
            {
                _preemptPendingSeq = -1;
            }
            return preemptOnStart;
        }
    }

    private async Task PlayAsync(
        QueuedJob queued,
        CancellationTokenSource jobCts,
        PlaybackSink sink,
        TimeProvider time,
        ILogger? logger,
        CancellationToken ct)
    {
        var job = queued.Job;
        var drained = false;
        long firstChunkTimestamp = 0;
        var totalAudio = TimeSpan.Zero;
        try
        {
            (firstChunkTimestamp, totalAudio) = await DrainAudioAsync(queued, jobCts.Token, sink, time, logger);
            logger?.LogInformation(
                "Playback job {Label} drained {Chunks} chunk(s)", job.Label, queued.ChunksWritten);
            drained = true;
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            queued.Settle(PlaybackOutcomeKind.Preempted);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One job's audio failing (TTS synthesis or a transient write) must not tear down the
            // whole playback loop: log it, surface it via the sink's OnError, and continue.
            logger?.LogWarning(
                ex, "Playback job {Label} failed after {Chunks} chunk(s)", job.Label, queued.ChunksWritten);
            if (sink.OnError is not null)
            {
                try
                {
                    await sink.OnError(job, ex);
                }
                catch (Exception oex)
                {
                    logger?.LogWarning(oex, "Playback onError handler threw for {Label}", job.Label);
                }
            }
            queued.Settle(PlaybackOutcomeKind.Failed, ex);
        }
        finally
        {
            await SendAudioStopAsync(queued, sink, logger, ct);
            if (drained)
            {
                await WaitOutRealtimeTailAsync(totalAudio, firstChunkTimestamp, time, ct);
                // Settled even when teardown cut the real-time tail short: the audio was written,
                // so the satellite has it. Reporting nothing at all there is what left a waiting
                // producer with no answer but its own timeout.
                queued.Settle(PlaybackOutcomeKind.Drained);
            }
            ReleaseCurrent(queued);
            jobCts.Dispose();
            // Whatever the job's audio was wrapped in is the queue's to release, on every way
            // the job ended — no producer disposes anything.
            await queued.ReleaseAudioAsync();
        }
    }

    private async Task<(long FirstChunkTimestamp, TimeSpan TotalAudio)> DrainAudioAsync(
        QueuedJob queued,
        CancellationToken jobToken,
        PlaybackSink sink,
        TimeProvider time,
        ILogger? logger)
    {
        var job = queued.Job;

        // A job marked preempt-on-start must not play a single chunk, and that cannot be left
        // to the audio source: WithCancellation only HANDS the token to the enumerable, so a
        // source that does not observe it (a buffered or synthetic stream) would drain
        // normally and the alarm would still wait behind it. Throwing here makes the decision
        // the loop's, and lands it on the preempted outcome.
        jobToken.ThrowIfCancellationRequested();

        long firstChunkTimestamp = 0;
        var totalAudio = TimeSpan.Zero;

        // Synthesis is lazy (the TTS enumerable is pulled here), so time it from just before
        // the first pull to the first chunk — not from enqueue, which is a near-zero channel write.
        var synthesisStart = time.GetTimestamp();
        await foreach (var chunk in job.Audio.WithCancellation(jobToken))
        {
            FirstAudioTiming? firstAudio = null;
            if (queued.ChunksWritten == 0)
            {
                firstChunkTimestamp = time.GetTimestamp();
                if (sink.OnAudioStart is not null)
                {
                    // The alert route is the alarm kind's, and only its. Priority is
                    // deliberately not the marker: confirmation prompts share High and must
                    // stay at the calibrated conversational level.
                    await sink.OnAudioStart(
                        chunk.Format, job.Kind == PlaybackKind.Alarm, jobToken);
                }
                if (job.OnFirstAudio is not null)
                {
                    firstAudio = BuildFirstAudioTiming(job, synthesisStart, firstChunkTimestamp, time);
                }
            }
            totalAudio += DurationOf(chunk);
            queued.ChunksWritten++;
            await sink.Writer(chunk, jobToken);
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

        return (firstChunkTimestamp, totalAudio);
    }

    private FirstAudioTiming BuildFirstAudioTiming(
        PlaybackJob job, long synthesisStart, long firstChunkTimestamp, TimeProvider time)
    {
        var turnStart = Interlocked.Read(ref _turnStartedAt);
        var speechEnd = Interlocked.Read(ref _speechEndedAt);
        return new FirstAudioTiming(
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

    // Close the playback envelope so the satellite flushes paplay (EOF on
    // disconnect_after_stop). Use the connection token: the job's token may be canceled
    // by preemption, but the satellite still needs the audio-stop. A bare
    // audio-start with no chunks gets no stop, matching Wyoming framing.
    private static async Task SendAudioStopAsync(
        QueuedJob queued, PlaybackSink sink, ILogger? logger, CancellationToken ct)
    {
        if (queued.ChunksWritten == 0 || sink.OnAudioStop is null || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await sink.OnAudioStop(ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to send audio-stop for {Label}", queued.Job.Label);
        }
    }

    // Drained means "the satellite finished PLAYING", not "we finished writing".
    // The Pi buffers the audio and plays PCM at real time, so wait out the remaining
    // nominal duration. Self-corrects for back-pressuring satellites (remaining <= 0).
    private async Task WaitOutRealtimeTailAsync(
        TimeSpan totalAudio, long firstChunkTimestamp, TimeProvider time, CancellationToken ct)
    {
        if (totalAudio <= TimeSpan.Zero || ct.IsCancellationRequested || _dropCts.IsCancellationRequested)
        {
            return;
        }

        var remaining = totalAudio - time.GetElapsedTime(firstChunkTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        // The run token ends the wait on shutdown; the drop token ends it when the link died and
        // the drain is waiting on this very loop.
        using var tail = CancellationTokenSource.CreateLinkedTokenSource(ct, _dropCts.Token);
        try
        {
            await Task.Delay(remaining, time, tail.Token);
        }
        catch (OperationCanceledException)
        {
            // Connection tearing down, or the link dropped mid-tail.
        }
    }

    private void ReleaseCurrent(QueuedJob queued)
    {
        lock (_gate)
        {
            _currentCts = null;
            // Left set when this job never reached a terminal path — the connection died
            // mid-job — so the drain can find it and settle it as discarded.
            if (queued.Completed.IsCompleted)
            {
                _inFlight = null;
            }
        }
    }

    // One job's place in the queue: its order, what to play, how much of it reached the writer, and
    // the single outcome that will end it. Settling is first-wins, which is what makes "exactly one
    // outcome" true even when the drain races a loop that was already finishing.
    private sealed class QueuedJob(long seq, PlaybackJob job, PrefetchedAudio? prefetch = null)
    {
        private readonly TaskCompletionSource<PlaybackOutcome> _settled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Seq => seq;
        public PlaybackJob Job => job;
        public int ChunksWritten;

        // Continuations run asynchronously, so a producer's reaction cannot capture the loop's
        // thread and delay the next job's audio.
        public Task<PlaybackOutcome> Completed => _settled.Task;

        public void Settle(PlaybackOutcomeKind kind, Exception? error = null) =>
            _settled.TrySetResult(new PlaybackOutcome(kind, ChunksWritten, error));

        // Idempotent: the disposal latches on its first call, so the loop's finally and the drain's
        // sweep can both release the same in-flight job, and a job that drained has nothing left to
        // cancel.
        public ValueTask ReleaseAudioAsync() => prefetch?.DisposeAsync() ?? ValueTask.CompletedTask;
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