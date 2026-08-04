using System.Runtime.CompilerServices;
using System.Text;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.McpChannelVoice.PlaybackFakes;

namespace Tests.Unit.McpChannelVoice;

// Ordering, preemption and the loop's own timing, driven against sources the loop pulls itself.
// Every queue here is built without a prefetch so a reply segment's audio is pulled when the loop
// reaches it: the prefetch is the subject of PlaybackQueueOutcomeTests, and having it run ahead of
// the loop would move a fake clock before the loop had taken its first reading.
public class PlaybackQueueTests
{
    [Fact]
    public async Task Enqueue_High_PreemptsSegmentsAlreadyQueuedBehindTheCurrentOne()
    {
        // A reply is several sentence jobs now, so cancelling only _currentCts left an alarm
        // queued behind the REST of the answer: it cut sentence 1 and was then heard after sentences
        // 2..N had played in full. Every job queued when the High job arrives must be preempted.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<AudioChunk> gated(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new AudioChunk
            { Data = Encoding.UTF8.GetBytes("s1"), Format = AudioFormat.WyomingStandard };
            firstChunkWritten.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }

        var segment1 = new PlaybackJob(
            Label: "reply-1",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: gated());
        var segment2 = segment1 with { Label = "reply-2", Audio = Audio("s2", count: 1) };
        var segment3 = segment1 with { Label = "reply-3", Audio = Audio("s3", count: 1) };
        var alarm = segment1 with
        {
            Label = "alarm",
            Priority = AnnouncePriority.High,
            Audio = Audio("alarm", count: 1)
        };

        var pumpTask = queue.RunAsync(
            async (chunk, _) =>
            {
                lock (played)
                { played.Add(Encoding.UTF8.GetString(chunk.Data.Span)); }
                await Task.Yield();
            },
            CancellationToken.None);

        var one = queue.Enqueue(segment1);
        var two = queue.Enqueue(segment2);
        var three = queue.Enqueue(segment3);
        await firstChunkWritten.Task;

        queue.Enqueue(alarm);
        queue.Complete();
        await pumpTask;

        // The alarm is heard next, not after the rest of the answer — and every segment that was
        // queued when it arrived says so itself.
        played.ShouldBe(["s1", "alarm"]);
        foreach (var segment in new[] { one, two, three })
        {
            (await segment.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        }
    }

    [Fact]
    public async Task Enqueue_SecondHighStackedBehindAHigh_StillPlays()
    {
        // The preempt mark must not swallow a second alarm that stacks in the gap — the High
        // exemption in the loop is what keeps insistent announcements ringing.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();

        var first = new PlaybackJob(
            Label: "alarm-1",
            Kind: PlaybackKind.Alarm,
            Priority: AnnouncePriority.High,
            Audio: Audio("alarm-1", count: 1));
        var second = first with { Label = "alarm-2", Audio = Audio("alarm-2", count: 1) };

        var pumpTask = queue.RunAsync(
            async (chunk, _) =>
            {
                lock (played)
                { played.Add(Encoding.UTF8.GetString(chunk.Data.Span)); }
                await Task.Yield();
            },
            CancellationToken.None);

        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.Complete();
        await pumpTask;

        played.ShouldBe(["alarm-1", "alarm-2"]);
    }

    [Fact]
    public async Task Enqueue_Normal_RunsAfterCurrent()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();

        var first = new PlaybackJob(
            Label: "first",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("first", count: 2));
        var second = first with { Label = "second", Audio = Audio("second", count: 1) };

        var pumpTask = queue.RunAsync(
            async (chunk, ct) =>
            {
                played.Add(Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None);

        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.Complete();

        await pumpTask;

        played.ShouldBe(["first", "first", "second"]);
    }

    [Fact]
    public async Task Enqueue_ReplySegments_GetTheReplyAllowanceNotTheAnnounceOne()
    {
        // An answer is several sentence jobs and is one logical unit: refusing part of it leaves a
        // hole in the middle of what the user hears. Its allowance is its own, and the kind is what
        // picks it — no producer passes a depth.
        var queue = new PlaybackQueue(replyMaxDepth: 3, announceMaxDepth: 1, prefetchBufferChunks: null);

        queue.Enqueue(Job("s1", PlaybackKind.Reply)).Refused.ShouldBeNull();
        queue.Enqueue(Job("s2", PlaybackKind.Reply)).Refused.ShouldBeNull();
        queue.Enqueue(Job("s3", PlaybackKind.Reply)).Refused.ShouldBeNull();
        queue.Enqueue(Job("s4", PlaybackKind.Reply)).Refused.ShouldNotBeNull();
    }

    [Fact]
    public async Task Enqueue_EverythingThatIsNotAReply_SharesTheAnnounceAllowance()
    {
        // The preamble cue plays ahead of an answer rather than being part of it, so it shares the
        // announce depth exactly as it did when the reply tool chose the limit itself.
        var queue = new PlaybackQueue(replyMaxDepth: 8, announceMaxDepth: 2, prefetchBufferChunks: null);

        queue.Enqueue(Job("announce", PlaybackKind.Announce)).Refused.ShouldBeNull();
        queue.Enqueue(Job("preamble", PlaybackKind.Preamble)).Refused.ShouldBeNull();
        queue.Enqueue(Job("approval", PlaybackKind.Approval)).Refused.ShouldNotBeNull();
    }

    [Fact]
    public async Task CanAccept_AnswersPerKind_SoTheReplyPathCanAskBeforeItSpendsItsText()
    {
        // TryTakeSpeakable removes a sentence run from the accumulator, so the reply path asks
        // before it takes the text rather than discovering a refusal after both text and synthesis
        // are spent.
        var queue = new PlaybackQueue(replyMaxDepth: 2, announceMaxDepth: 1, prefetchBufferChunks: null);
        queue.Enqueue(Job("s1", PlaybackKind.Reply));

        queue.CanAccept(PlaybackKind.Reply).ShouldBeTrue();
        queue.CanAccept(PlaybackKind.Announce).ShouldBeFalse();
    }

    [Fact]
    public async Task Enqueue_HighPriorityWhileIdle_PreemptsQueuedAheadButPlaysItself()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);

        // Enqueue a normal job then a high job BEFORE the loop runs. When the high job is enqueued no
        // job is marked current, exercising the dequeue->assign gap / idle preempt-sequence path: the
        // already-queued normal must be preempted, while the high job must still play (a high job must
        // never preempt itself).
        var normal = new PlaybackJob(
            Label: "normal",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("normal", count: 2));
        var high = new PlaybackJob(
            Label: "high",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.High,
            Audio: Audio("high", count: 1));

        var queuedAhead = queue.Enqueue(normal);
        var cuttingIn = queue.Enqueue(high);
        queue.Complete();

        await queue.RunAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            CancellationToken.None);

        (await queuedAhead.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Preempted);
        (await cuttingIn.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    [Fact]
    public async Task Run_JobAudioThrows_SurvivesAndReportsThenPlaysNext()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var played = new List<string>();
        var errors = new List<string>();

        var failing = new PlaybackJob(
            Label: "failing",
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.Normal,
            Audio: ThrowingAudio());
        var next = failing with { Label = "next", Audio = Audio("next", count: 1) };

        var pumpTask = queue.RunAsync(
            async (chunk, ct) =>
            {
                played.Add(Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None,
            onError: (job, ex) =>
            {
                errors.Add(job.Label);
                return Task.CompletedTask;
            });

        queue.Enqueue(failing);
        queue.Enqueue(next);
        queue.Complete();

        await pumpTask;

        errors.ShouldBe(["failing"]);
        played.ShouldBe(["next"]);
    }

    // "a job that drains settles drained" and "a preempted job never settles drained" are the
    // exactly-one-outcome guarantee, proved once in PlaybackQueueOutcomeTests.

    // The two turn-handshake tests that used to sit here moved to VoiceTurnTests: they test the
    // handshake, not playback, and they now drive the real path (begin a segment, complete it, end
    // the stream) rather than a signal method that no longer exists.

    [Fact]
    public async Task Run_WaitsForAudioPlaybackDuration_BeforeSettlingDrained()
    {
        // Drained means the satellite finished PLAYING, not that the hub finished writing: the Pi
        // buffers the audio and plays it at real time, and the earcon's mic must not open early.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // 16000 bytes at 16 kHz/16-bit/mono = exactly 500 ms of audio.
        static async IAsyncEnumerable<AudioChunk> halfSecond()
        {
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: halfSecond());

        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);

        var ticket = queue.Enqueue(job);
        await Task.Delay(80); // let the loop write the audio and reach the playback wait
        ticket.Completed.IsCompleted.ShouldBeFalse(); // the 500 ms of audio has not played out yet

        time.Advance(TimeSpan.FromMilliseconds(500)); // playback completes
        (await ticket.Completed.WaitAsync(TimeSpan.FromSeconds(2)))
            .Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        queue.Complete();
        await pump;
    }

    [Fact]
    public async Task Run_FirstChunk_PublishesSynthesisAndTurnTiming()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.MarkTurnStart(time.GetTimestamp());
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
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: audio(),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);
        queue.Enqueue(job);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // TTS latency = synthesis request -> first audio chunk (300 ms), independent of the
        // pre-synthesis turn time. Wake/turn -> first audio = 2000 + 300 = 2300 ms.
        timing.SinceSynthesisStart.ShouldBe(TimeSpan.FromMilliseconds(300));
        timing.SinceTurnStart.ShouldBe(TimeSpan.FromMilliseconds(2300));

        queue.Complete();
        await Task.Delay(80);                            // let the loop reach the playback-drain wait
        time.Advance(TimeSpan.FromSeconds(1));           // drain the remaining playback duration
        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_FirstChunk_NoTurnStart_TurnTimingNull()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        // No MarkTurnStart: a job with no preceding turn (e.g. not wired) must NOT report a turn time,
        // so WakeToFirstAudioMs is simply not published rather than emitting a garbage value.
        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 1),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        queue.Enqueue(job);
        queue.Complete();

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceTurnStart.ShouldBeNull();
        timing.SinceSynthesisStart.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);

        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_MultiChunk_InvokesOnFirstAudioOnce()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var invocations = 0;

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("x", count: 3),
            OnFirstAudio: _ => { Interlocked.Increment(ref invocations); return Task.CompletedTask; });

        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        queue.Enqueue(job);
        queue.Complete();
        await pump;

        invocations.ShouldBe(1); // fires only on the first chunk, not per chunk
    }

    [Fact]
    public async Task Enqueue_TwoHighWhileIdle_BothPlay()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);

        // Two High jobs enqueued while idle (no job marked current). The second must NOT preempt the
        // first via the pending high-water mark; both play in FIFO order (regression guard for the
        // preempt-sequence fix).
        PlaybackJob high(string label)
        {
            return new(
            Label: label,
            Kind: PlaybackKind.Announce,
            Priority: AnnouncePriority.High,
            Audio: Audio(label, count: 1));
        }

        var first = queue.Enqueue(high("h1"));
        var second = queue.Enqueue(high("h2"));
        queue.Complete();

        await queue.RunAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            CancellationToken.None);

        (await first.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
        (await second.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    // Enqueueing onto a completed queue is a refusal rather than a ChannelClosedException — see
    // PlaybackQueueOutcomeTests, which owns every refusal reason.

    [Fact]
    public async Task Run_FirstChunk_PublishesSpeechEndAndQueueWaitTiming()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.MarkTurnStart(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(3));            // the user talking
        queue.MarkSpeechEnd(time.GetTimestamp(), endpointTailMs: 0, time);
        time.Advance(TimeSpan.FromSeconds(2));            // verify + STT + agent
        var enqueuedAt = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(400));     // the reply waits behind the preamble

        // Synthesis takes 300 ms to produce its first chunk; 16000 bytes = 500 ms of audio.
        async IAsyncEnumerable<AudioChunk> audio()
        {
            time.Advance(TimeSpan.FromMilliseconds(300));
            yield return new AudioChunk { Data = new byte[16000], Format = AudioFormat.WyomingStandard };
            await Task.CompletedTask;
        }

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: audio(),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; },
            EnqueuedAt: enqueuedAt);

        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);
        queue.Enqueue(job);

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Speech end -> first audio excludes the 3 s the user spent talking: 2000 + 400 + 300.
        timing.SinceSpeechEnd.ShouldBe(TimeSpan.FromMilliseconds(2700));
        // Queue wait ends at synthesis start, so it is the 400 ms behind the preamble — NOT the
        // 300 ms of synthesis, which SinceSynthesisStart already owns.
        timing.QueueWait.ShouldBe(TimeSpan.FromMilliseconds(400));
        timing.SinceSynthesisStart.ShouldBe(TimeSpan.FromMilliseconds(300));

        queue.Complete();
        await Task.Delay(80);                            // let the loop reach the playback-drain wait
        time.Advance(TimeSpan.FromSeconds(1));           // drain the remaining playback duration
        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_FirstChunk_WritesTheAudioBeforeInvokingOnFirstAudio()
    {
        // Four awaited metric publishes now hang off OnFirstAudio, so invoking it before the first
        // writer call delays the first audio byte reaching the satellite by however long Redis takes:
        // the observer changing what it observes. Every timestamp the callback reports is captured
        // before the write, so ordering the write first costs no accuracy at all.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var order = new List<string>();

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 2),
            OnFirstAudio: _ => { order.Add("metrics"); return Task.CompletedTask; });

        var pump = queue.RunAsync(
            (_, _) => { order.Add("write"); return Task.CompletedTask; }, CancellationToken.None);
        queue.Enqueue(job);
        queue.Complete();
        await pump.WaitAsync(TimeSpan.FromSeconds(2));

        order.ShouldBe(["write", "metrics", "write"]);
    }

    [Fact]
    public async Task Run_FirstChunk_SpeechEndAnchorRewindsTheEndpointTail()
    {
        // The caller can only see the capture CLOSE, which SilenceGate reaches a whole
        // trailingSilence run after the user stopped talking (2000 ms in production). The tail is
        // machine time the user waits through, so it belongs inside this span: without the rewind
        // SpeechEndToFirstAudioMs omits it and EndpointTailMs sits beside the span instead of nested
        // inside it, which is ~40% of the wait at production settings.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        time.Advance(TimeSpan.FromSeconds(3));            // the user talking
        time.Advance(TimeSpan.FromMilliseconds(2000));    // the endpointing tail the gate waits out
        queue.MarkSpeechEnd(time.GetTimestamp(), endpointTailMs: 2000, time);
        time.Advance(TimeSpan.FromMilliseconds(1000));    // verify + STT + agent

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 1),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None, time);
        queue.Enqueue(job);
        queue.Complete();

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceSpeechEnd.ShouldBe(TimeSpan.FromMilliseconds(3000)); // 2000 tail + 1000 machine

        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_FirstChunk_NoSpeechEndOrEnqueueStamp_TimingsAreNull()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var fired = new TaskCompletionSource<FirstAudioTiming>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A job with no preceding capture (chime, announce) must report nulls rather than a garbage
        // value, so the hub simply publishes nothing for those spans.
        var job = new PlaybackJob(
            Label: "chime:kitchen-01",
            Kind: PlaybackKind.Chime,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 1),
            OnFirstAudio: t => { fired.TrySetResult(t); return Task.CompletedTask; });

        var pump = queue.RunAsync(async (_, _) => await Task.Yield(), CancellationToken.None);
        queue.Enqueue(job);
        queue.Complete();

        var timing = await fired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        timing.SinceSpeechEnd.ShouldBeNull();
        timing.QueueWait.ShouldBeNull();

        await pump.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_FirstAudioCallbackThrows_TheJobStillDrains()
    {
        // The one callback a job still carries is invoked between two audio writes, so it keeps its
        // guard: a producer's metrics publish failing must not cut the answer short.
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var written = 0;

        var job = new PlaybackJob(
            Label: "reply:kitchen-01",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("hi", count: 3),
            OnFirstAudio: _ => throw new InvalidOperationException("metrics down"));

        var ticket = queue.Enqueue(job);
        queue.Complete();
        await queue.RunAsync((_, _) => { written++; return Task.CompletedTask; }, CancellationToken.None);

        written.ShouldBe(3);
        (await ticket.Completed).Kind.ShouldBe(PlaybackOutcomeKind.Drained);
    }

    // The alert bit is what the hub puts on the wire for the satellite's sink selection, so it has
    // to survive the queue and arrive with the stream it belongs to — not with a neighbouring job.
    // It follows from the alarm kind, so a producer cannot set it on something that is not an alert.
    [Fact]
    public async Task Run_ReportsTheAlarmKindAsTheAlertRouteOnAudioStart()
    {
        var queue = new PlaybackQueue(prefetchBufferChunks: null);
        var flags = new List<bool>();

        var pumpTask = queue.RunAsync(
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            onAudioStart: (_, alert, _) =>
            {
                lock (flags)
                { flags.Add(alert); }
                return Task.CompletedTask;
            });

        var reply = new PlaybackJob(
            Label: "reply",
            Kind: PlaybackKind.Reply,
            Priority: AnnouncePriority.Normal,
            Audio: Audio("reply", count: 1));
        var alarm = reply with
        {
            Label = "alarm",
            Kind = PlaybackKind.Alarm,
            Audio = Audio("alarm", count: 1)
        };

        queue.Enqueue(reply);
        queue.Enqueue(alarm);
        queue.Complete();
        await pumpTask;

        flags.ShouldBe(new[] { false, true });
    }

}