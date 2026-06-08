using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionPlaybackTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public async Task EnqueuePlayback_Normal_RunsAfterCurrent()
    {
        var session = MakeSession();
        var played = new List<string>();

        var first = new PlaybackJob(
            Label: "first",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("first", count: 2),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        var second = first with { Label = "second", Audio = GenerateAudio("second", count: 1) };

        var pumpTask = session.RunPlaybackLoopAsync(
            async (chunk, ct) =>
            {
                played.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None);

        await session.EnqueuePlaybackAsync(first, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(second, queueMaxDepth: 4);
        session.CompletePlayback();

        await pumpTask;

        played.ShouldBe(["first", "first", "second"]);
    }

    [Fact]
    public async Task EnqueuePlayback_LowPriorityWhileQueueNonEmpty_IsDropped()
    {
        // The returned bool is observable behavior: AnnouncementService maps it to
        // Status queued/dropped + the AnnounceQueued/AnnounceError metric. A Low-priority job must
        // be dropped (return false) when anything is already queued, so it never delays speech.
        var session = MakeSession();
        var normal = new PlaybackJob(
            Label: "normal",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("normal", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        var low = normal with { Label = "low", Priority = AnnouncePriority.Low };

        // No playback loop is running, so the first job stays queued (Reader.Count > 0).
        (await session.EnqueuePlaybackAsync(normal, queueMaxDepth: 4)).ShouldBeTrue();
        (await session.EnqueuePlaybackAsync(low, queueMaxDepth: 4)).ShouldBeFalse();
    }

    [Fact]
    public async Task EnqueuePlayback_NormalWhenQueueAtMaxDepth_IsDropped()
    {
        // The depth cap is the backpressure guard: once the queue is full, further Normal jobs
        // must be dropped (return false) rather than unbounded-buffered.
        var session = MakeSession();
        static PlaybackJob job(string label)
        {
            return new(
            Label: label,
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio(label, count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        }

        // No loop running: fill to depth 1, then the next Normal overflows.
        (await session.EnqueuePlaybackAsync(job("a"), queueMaxDepth: 1)).ShouldBeTrue();
        (await session.EnqueuePlaybackAsync(job("b"), queueMaxDepth: 1)).ShouldBeFalse();
    }

    [Fact]
    public async Task EnqueuePlayback_HighPriorityWhileIdle_PreemptsQueuedAheadButPlaysItself()
    {
        var session = MakeSession();
        var drained = new List<string>();
        var preempted = new List<string>();

        // Enqueue a normal job then a high job BEFORE the loop runs. When the high job is enqueued no
        // job is marked current, exercising the dequeue->assign gap / idle preempt-sequence path: the
        // already-queued normal must be preempted, while the high job must still play (a high job must
        // never preempt itself).
        var normal = new PlaybackJob(
            Label: "normal",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("normal", count: 2),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: l => { preempted.Add(l); return Task.CompletedTask; },
            OnDrained: () => { drained.Add("normal"); return Task.CompletedTask; });
        var high = new PlaybackJob(
            Label: "high",
            Priority: AnnouncePriority.High,
            Audio: GenerateAudio("high", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: l => { preempted.Add(l); return Task.CompletedTask; },
            OnDrained: () => { drained.Add("high"); return Task.CompletedTask; });

        await session.EnqueuePlaybackAsync(normal, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(high, queueMaxDepth: 4);
        session.CompletePlayback();

        await session.RunPlaybackLoopAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            CancellationToken.None);

        preempted.ShouldBe(["normal"]);
        drained.ShouldBe(["high"]);
    }

    [Fact]
    public async Task RunPlaybackLoop_JobAudioThrows_SurvivesAndReportsThenPlaysNext()
    {
        var session = MakeSession();
        var played = new List<string>();
        var errors = new List<string>();

        var failing = new PlaybackJob(
            Label: "failing",
            Priority: AnnouncePriority.Normal,
            Audio: ThrowingAudio(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        var next = failing with { Label = "next", Audio = GenerateAudio("next", count: 1) };

        var pumpTask = session.RunPlaybackLoopAsync(
            async (chunk, ct) =>
            {
                played.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None,
            onError: (job, ex) =>
            {
                errors.Add(job.Label);
                return Task.CompletedTask;
            });

        await session.EnqueuePlaybackAsync(failing, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(next, queueMaxDepth: 4);
        session.CompletePlayback();

        await pumpTask;

        errors.ShouldBe(["failing"]);
        played.ShouldBe(["next"]);
    }

    [Fact]
    public async Task RunPlaybackLoop_OnStartedThrows_SwallowsAndKeepsLoopAlive()
    {
        var session = MakeSession();
        var played = new List<string>();

        var bad = new PlaybackJob(
            Label: "bad-onstarted",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("bad", count: 1),
            OnStarted: _ => throw new InvalidOperationException("metrics down"),
            OnPreempted: _ => Task.CompletedTask);
        var next = bad with
        {
            Label = "next",
            Audio = GenerateAudio("next", count: 1),
            OnStarted = _ => Task.CompletedTask
        };

        var pumpTask = session.RunPlaybackLoopAsync(
            async (chunk, ct) =>
            {
                played.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None);

        await session.EnqueuePlaybackAsync(bad, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(next, queueMaxDepth: 4);
        session.CompletePlayback();

        await pumpTask;

        // A failing OnStarted (e.g. metrics publish down) is swallowed: the job's audio still plays
        // and the loop continues to the next job rather than tearing down.
        played.ShouldBe(["bad", "next"]);
    }

    [Fact]
    public async Task RunPlaybackLoop_JobDrains_InvokesOnDrained()
    {
        var session = MakeSession();
        var drained = new List<string>();

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("hi", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnDrained: () => { drained.Add("reply:kitchen-01"); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(
            async (_, _) => await Task.Yield(), CancellationToken.None);

        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);
        session.CompletePlayback();
        await pump;

        drained.ShouldBe(["reply:kitchen-01"]);
    }

    [Fact]
    public async Task RunPlaybackLoop_JobPreempted_DoesNotInvokeOnDrained()
    {
        var session = MakeSession();
        var drained = new List<string>();
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> gated(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            firstChunkWritten.TrySetResult();
            // Block mid-drain until preempt cancels the job token. The only exit is cancellation,
            // which throws OperationCanceledException here, so the second chunk never yields and the
            // drain never completes normally — exactly the preemption path we are asserting.
            await Task.Delay(Timeout.Infinite, token);
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: gated(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnDrained: () => { drained.Add("reply:kitchen-01"); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None);

        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);
        await firstChunkWritten.Task;       // job is mid-drain
        session.PreemptCurrent();           // cancel it; the gated enumerator unwinds via OCE
        session.CompletePlayback();
        await pump;

        drained.ShouldBeEmpty();            // OnDrained must NOT fire on preempt
    }

    [Fact]
    public async Task TurnHandshake_SignalSpoken_ResolvesTrue()
    {
        var session = MakeSession();
        session.ResetTurn();
        var wait = session.WaitForTurnSpokenAsync();
        session.SignalTurnSpoken();
        (await wait).ShouldBeTrue();
    }

    [Fact]
    public async Task TurnHandshake_SignalSilent_ResolvesFalse()
    {
        var session = MakeSession();
        session.ResetTurn();
        var wait = session.WaitForTurnSpokenAsync();
        session.SignalTurnSilent();
        (await wait).ShouldBeFalse();
    }

    [Fact]
    public async Task MicRouting_RouteAudio_FeedsActiveCaptureOnly()
    {
        var session = MakeSession();
        var loud = new byte[3200];
        for (var i = 0; i < loud.Length; i += 2)
        { loud[i] = 0x40; loud[i + 1] = 0x1F; }
        AudioChunk loudChunk()
        {
            return new() { Data = loud, Format = AudioFormat.WyomingStandard };
        }

        var silent = new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard };

        // No active capture: routing is a safe no-op.
        Should.NotThrow(() => session.RouteAudio(silent));

        var capture = session.OpenCapture(new SilenceGate(
            rmsThreshold: 500,
            trailingSilence: TimeSpan.FromMilliseconds(200),
            maxUtterance: TimeSpan.FromMilliseconds(1000),
            minSpeech: TimeSpan.FromMilliseconds(100)));

        // Speech then trailing silence routed through the session must end the active capture.
        session.RouteAudio(loudChunk());
        session.RouteAudio(loudChunk());
        session.RouteAudio(silent);
        session.RouteAudio(silent);

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        // After close, routing must not reach any capture (no throw, no effect).
        session.CloseCapture();
        Should.NotThrow(() => session.RouteAudio(loudChunk()));
    }

    [Fact]
    public async Task RunPlaybackLoop_WaitsForAudioPlaybackDuration_BeforeOnDrained()
    {
        var session = MakeSession();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 16000 bytes at 16 kHz/16-bit/mono = exactly 500 ms of audio.
        static async IAsyncEnumerable<AudioChunk> halfSecond()
        {
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: halfSecond(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnDrained: () => { drained.TrySetResult(); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);

        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);
        await Task.Delay(80); // let the loop write the audio and reach the playback wait
        drained.Task.IsCompleted.ShouldBeFalse(); // must NOT fire on write-drain — playback (500 ms) hasn't elapsed

        time.Advance(TimeSpan.FromMilliseconds(500)); // playback completes
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(2)); // now OnDrained fires
        session.CompletePlayback();
        await pump;
    }

    [Fact]
    public async Task RunPlaybackLoop_FirstChunk_PublishesSynthesisAndTurnTiming()
    {
        var session = MakeSession();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.MarkTurnStart(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(2)); // capture + STT + agent thinking before synthesis begins

        // Synthesis takes 300 ms to produce its first chunk; 16000 bytes = 500 ms of audio.
        async IAsyncEnumerable<AudioChunk> audio()
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: audio(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);
        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // TTS latency = synthesis request -> first audio chunk (300 ms), independent of the
        // pre-synthesis turn time. Wake/turn -> first audio = 2000 + 300 = 2300 ms.
        timing.SinceSynthesisStart.ShouldBe(TimeSpan.FromMilliseconds(300));
        timing.SinceTurnStart.ShouldBe(TimeSpan.FromMilliseconds(2300));

        session.CompletePlayback();
        await Task.Delay(80);                            // let the loop reach the playback-drain wait
        time.Advance(TimeSpan.FromSeconds(1));           // drain the remaining playback duration
        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunPlaybackLoop_FirstChunk_NoTurnStart_TurnTimingNull()
    {
        var session = MakeSession();
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        // No MarkTurnStart: a job with no preceding turn (e.g. not wired) must NOT report a turn time,
        // so WakeToFirstAudioMs is simply not published rather than emitting a garbage value.
        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("hi", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);
        session.CompletePlayback();

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceTurnStart.ShouldBeNull();
        timing.SinceSynthesisStart.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);

        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RunPlaybackLoop_MultiChunk_InvokesOnFirstAudioOnce()
    {
        var session = MakeSession();
        var invocations = 0;

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("x", count: 3),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnFirstAudio: _ => { Interlocked.Increment(ref invocations); return Task.CompletedTask; });

        var pump = session.RunPlaybackLoopAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);
        session.CompletePlayback();
        await pump;

        invocations.ShouldBe(1); // fires only on the first chunk, not per chunk
    }

    [Fact]
    public async Task RunPlaybackLoop_JobAudioThrows_InvokesOnFailed()
    {
        var session = MakeSession();
        var failed = new TaskCompletionSource();

        // A synthesis failure must reach OnFailed so awaiters (approval prompt, chime) that block on a
        // drained handshake are released instead of hanging forever.
        var failing = new PlaybackJob(
            Label: "failing",
            Priority: AnnouncePriority.Normal,
            Audio: ThrowingAudio(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask,
            OnFailed: _ => { failed.TrySetResult(); return Task.CompletedTask; });

        await session.EnqueuePlaybackAsync(failing, queueMaxDepth: 4);
        session.CompletePlayback();

        await session.RunPlaybackLoopAsync((_, _) => Task.CompletedTask, CancellationToken.None);

        failed.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task EnqueuePlayback_TwoHighWhileIdle_BothPlay()
    {
        var session = MakeSession();
        var drained = new List<string>();
        var preempted = new List<string>();

        // Two High jobs enqueued while idle (no job marked current). The second must NOT preempt the
        // first via the pending high-water mark; both play in FIFO order (regression guard for the
        // preempt-sequence fix).
        PlaybackJob high(string label)
        {
            return new(
            Label: label,
            Priority: AnnouncePriority.High,
            Audio: GenerateAudio(label, count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: l => { preempted.Add(l); return Task.CompletedTask; },
            OnDrained: () => { drained.Add(label); return Task.CompletedTask; });
        }

        await session.EnqueuePlaybackAsync(high("h1"), queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(high("h2"), queueMaxDepth: 4);
        session.CompletePlayback();

        await session.RunPlaybackLoopAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            CancellationToken.None);

        drained.ShouldBe(["h1", "h2"]);
        preempted.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnqueuePlayback_AfterChannelCompleted_ReturnsFalse()
    {
        var session = MakeSession();
        session.CompletePlayback(); // satellite disconnected -> playback channel completed

        var job = new PlaybackJob(
            Label: "x",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("x", count: 1),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);

        // Must return false (dropped) rather than throwing ChannelClosedException, so callers
        // like the announce endpoint don't surface a 500.
        var accepted = await session.EnqueuePlaybackAsync(job, queueMaxDepth: 4);

        accepted.ShouldBeFalse();
    }

    private static async IAsyncEnumerable<AudioChunk> GenerateAudio(string label, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new AudioChunk
            {
                Data = System.Text.Encoding.UTF8.GetBytes(label),
                Format = AudioFormat.WyomingStandard
            };
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AudioChunk> ThrowingAudio()
    {
        await Task.Yield();
        throw new InvalidOperationException("synthesis failed");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}