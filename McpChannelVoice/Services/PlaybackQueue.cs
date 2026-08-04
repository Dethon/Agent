using System.Threading.Channels;
using Domain.DTOs.Voice;

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

// Label stays free text for logs; Kind is what the queue reads.
public sealed record PlaybackJob(
    string Label,
    PlaybackKind Kind,
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

// What a satellite hears next: everything queued for playback on one connection, played one job at a
// time in the order it was accepted, with a high-priority job cutting in. One queue per satellite
// connection — the connection constructs it, runs its loop and completes it as the link unwinds; the
// satellite session exposes it as a property so producers reach it without a pass-through layer.
// The two depth limits are the queue's, not a producer's: an answer's segments get their own
// allowance (sharing the announce depth meant one turn's answer competed with itself and lost
// sentences out of its middle), everything else shares the announce one. The defaults match the
// settings records; the connection passes the configured values.
public sealed class PlaybackQueue(int replyMaxDepth = 64, int announceMaxDepth = 8)
{
    private readonly Channel<(long Seq, PlaybackJob Job)> _jobs =
        Channel.CreateUnbounded<(long Seq, PlaybackJob Job)>();
    private CancellationTokenSource? _currentCts;
    private readonly Lock _gate = new();
    private long _enqueueSeq;
    // High-water sequence whose jobs must be preempted as they start. Set only when a high-priority
    // job arrives while no job is marked current (the gap between dequeue and assignment, or idle),
    // so a preemption can't be lost to that race. High-priority jobs are exempt from this mark (see
    // the loop), so a second High job stacking in the same window never preempts the first.
    private long _preemptPendingSeq = -1;
    private const long TurnNotStarted = long.MinValue;
    private long _turnStartedAt = TurnNotStarted;
    private const long SpeechEndNotMarked = long.MinValue;
    private long _speechEndedAt = SpeechEndNotMarked;

    private int Depth => _jobs.Reader.Count;

    // Lets a caller decide whether a job of this kind can be queued BEFORE it consumes the text and
    // starts its synthesis, rather than finding out after both are already spent. It answers for the
    // kind, not for a particular job: the limit is the only thing it can know in advance.
    public bool CanAccept(PlaybackKind kind) => Depth < MaxDepthFor(kind);

    // Only an answer's segments get the reply allowance. The preamble cue is one job that plays
    // ahead of an answer, not part of it, so it shares the announce depth as it always did.
    private int MaxDepthFor(PlaybackKind kind) =>
        kind == PlaybackKind.Reply ? replyMaxDepth : announceMaxDepth;

    public ValueTask<bool> EnqueueAsync(PlaybackJob job)
    {
        if (job.Priority == AnnouncePriority.High)
        {
            long seq;
            lock (_gate)
            {
                // Mark EVERY job queued so far, then cancel the in-flight one. Cancelling only the
                // current job was enough when a reply was a single job; now that it is several
                // sentence jobs, an alarm cut sentence one and was then heard only after sentences
                // 2..N had played in full — and a queued alarm the user had already acknowledged
                // still rang, because dismissal preempts the current job. The mark is taken before
                // this job's seq is issued, so its own seq exceeds it and the loop's High exemption
                // keeps a stacked second High playing. It also closes the original race, where
                // _currentCts is momentarily null during the dequeue->assign gap.
                _preemptPendingSeq = _enqueueSeq;
                _currentCts?.Cancel();
                seq = ++_enqueueSeq;
            }
            // TryWrite (unbounded channel) returns false only once the writer is completed — i.e. the
            // satellite disconnected and the playback loop tore down. Returning false instead of
            // throwing ChannelClosedException lets callers (e.g. announce) record a dropped target.
            return ValueTask.FromResult(_jobs.Writer.TryWrite((seq, job)));
        }

        if (job.Priority == AnnouncePriority.Low && _jobs.Reader.Count > 0)
        {
            return ValueTask.FromResult(false);
        }

        if (!CanAccept(job.Kind))
        {
            return ValueTask.FromResult(false);
        }

        long normalSeq;
        lock (_gate)
        {
            normalSeq = ++_enqueueSeq;
        }
        return ValueTask.FromResult(_jobs.Writer.TryWrite((normalSeq, job)));
    }

    public void Complete() => _jobs.Writer.TryComplete();

    public void PreemptCurrent()
    {
        lock (_gate)
        {
            _currentCts?.Cancel();
        }
    }

    // Records the timestamp (from the loop's TimeProvider) at which the current user turn began, so
    // the loop can report wake/turn -> first-audio latency. Set at capture-open each turn.
    public void MarkTurnStart(long timestamp) => Interlocked.Exchange(ref _turnStartedAt, timestamp);

    // Records when the user stopped talking — everything after this is machine time they wait
    // through. The caller can only observe the CLOSE of the capture, which is a whole endpointing
    // tail later: SilenceGate only concludes "speech ended" once trailingSilence (1.2 s in production)
    // of silence has run. Rewinding by that frozen tail is what makes this the instant the user
    // actually stopped, so EndpointTailMs nests INSIDE SpeechEndToFirstAudioMs instead of sitting
    // before it and the turn decomposition sums. Legitimate because mic audio arrives in real time,
    // so the tail's audio-domain length is also its wall-clock length; the only residual error is
    // the gate-decision -> capture-close handoff. Stamped with the same TimeProvider the loop reads,
    // exactly like MarkTurnStart, so the two spans are comparable.
    public void MarkSpeechEnd(long captureClosedAt, long endpointTailMs, TimeProvider time) =>
        Interlocked.Exchange(
            ref _speechEndedAt, captureClosedAt - (endpointTailMs * time.TimestampFrequency / 1000));

    public async Task RunAsync(
        Func<AudioChunk, CancellationToken, Task> writer,
        CancellationToken ct,
        TimeProvider? time = null,
        ILogger? logger = null,
        Func<AudioFormat, bool, CancellationToken, Task>? onAudioStart = null,
        Func<CancellationToken, Task>? onAudioStop = null,
        Func<PlaybackJob, Exception, Task>? onError = null)
    {
        time ??= TimeProvider.System;
        await foreach (var (seq, job) in _jobs.Reader.ReadAllAsync(ct))
        {
            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bool preemptOnStart;
            lock (_gate)
            {
                _currentCts = jobCts;
                // Exempt High jobs: the mark exists to drop lower-priority jobs queued ahead of a
                // High request; a second High stacking in the gap must still play, not be preempted.
                preemptOnStart = _preemptPendingSeq >= 0 && seq <= _preemptPendingSeq
                    && job.Priority != AnnouncePriority.High;
                // Cleared only once the queue has drained PAST the mark, so every job that was
                // already queued when the High job arrived is preempted — not just the first one
                // dequeued after it.
                if (seq > _preemptPendingSeq)
                {
                    _preemptPendingSeq = -1;
                }
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
                // A job marked preempt-on-start must not play a single chunk, and that cannot be left
                // to the audio source: WithCancellation only HANDS the token to the enumerable, so a
                // source that does not observe it (a buffered or synthetic stream) would drain
                // normally and the alarm would still wait behind it. Throwing here makes the decision
                // the loop's, and lands it on the OnPreempted path below.
                jobCts.Token.ThrowIfCancellationRequested();

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
                            // The alert route is the alarm kind's, and only its. Priority is
                            // deliberately not the marker: confirmation prompts share High and must
                            // stay at the calibrated conversational level.
                            await onAudioStart(
                                chunk.Format, job.Kind == PlaybackKind.Alarm, jobCts.Token);
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
                { _currentCts = null; }
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