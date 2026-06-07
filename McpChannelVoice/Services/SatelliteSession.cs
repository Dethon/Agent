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
    Func<Exception, Task>? OnFailed = null);

// Timing captured the moment a job's first audio chunk is produced. SinceSynthesisStart is the
// TTS time-to-first-audio (synthesis request -> first chunk); SinceTurnStart is the wake/turn-open
// -> first audio latency, null when the job had no preceding user turn.
public sealed record FirstAudioTiming(TimeSpan SinceSynthesisStart, TimeSpan? SinceTurnStart);

public sealed class SatelliteSession
{
    private readonly Channel<(long Seq, PlaybackJob Job)> _playback =
        Channel.CreateUnbounded<(long Seq, PlaybackJob Job)>();
    private CancellationTokenSource? _currentPlaybackCts;
    private readonly Lock _gate = new();
    private long _enqueueSeq;
    // High-water sequence whose jobs must be preempted as they start. Set only when a high-priority
    // job arrives while no job is marked current (the gap between dequeue and assignment, or idle),
    // so a preemption can't be lost to that race. The high job claims a later sequence, so it is
    // never preempted by its own request.
    private long _preemptPendingSeq = -1;
    private UtteranceCapture? _capture;
    private readonly Lock _turnGate = new();
    private TaskCompletionSource<bool> _turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const long TurnNotStarted = long.MinValue;
    private long _turnStartedAt = TurnNotStarted;

    public SatelliteSession(string satelliteId, SatelliteConfig config)
    {
        SatelliteId = satelliteId;
        Config = config;
    }

    public string SatelliteId { get; }
    public SatelliteConfig Config { get; }

    public async ValueTask<bool> EnqueuePlaybackAsync(PlaybackJob job, int queueMaxDepth)
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
            await _playback.Writer.WriteAsync((seq, job));
            return true;
        }

        if (job.Priority == AnnouncePriority.Low && _playback.Reader.Count > 0)
        {
            return false;
        }

        if (_playback.Reader.Count >= queueMaxDepth)
        {
            return false;
        }

        long normalSeq;
        lock (_gate)
        {
            normalSeq = ++_enqueueSeq;
        }
        await _playback.Writer.WriteAsync((normalSeq, job));
        return true;
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

    // Callers must ResetTurn before the reply path can SignalTurnSpoken/SignalTurnSilent for
    // the new turn; otherwise a signal lands on the discarded TCS and the awaiter blocks forever.
    public void ResetTurn()
    {
        lock (_turnGate)
        {
            _turn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

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
                preemptOnStart = _preemptPendingSeq >= 0 && seq <= _preemptPendingSeq;
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
                            var timing = new FirstAudioTiming(
                                time.GetElapsedTime(synthesisStart, firstChunkTimestamp),
                                turnStart == TurnNotStarted
                                    ? null
                                    : time.GetElapsedTime(turnStart, firstChunkTimestamp));
                            // A failing metrics publish must neither abort playback nor tear down the loop.
                            try
                            {
                                await job.OnFirstAudio(timing);
                            }
                            catch (Exception ex)
                            {
                                logger?.LogWarning(ex, "Playback OnFirstAudio callback failed for {Label}", job.Label);
                            }
                        }
                    }
                    totalAudio += DurationOf(chunk);
                    chunks++;
                    await writer(chunk, jobCts.Token);
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