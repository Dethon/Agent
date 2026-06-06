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

        async IAsyncEnumerable<AudioChunk> Gated(
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
            Audio: Gated(),
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
        AudioChunk Loud() => new() { Data = loud, Format = AudioFormat.WyomingStandard };
        var silent = new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard };

        // No active capture: routing is a safe no-op.
        Should.NotThrow(() => session.RouteAudio(silent));

        var capture = session.OpenCapture(new SilenceGate(
            rmsThreshold: 500,
            trailingSilence: TimeSpan.FromMilliseconds(200),
            maxUtterance: TimeSpan.FromMilliseconds(1000),
            minSpeech: TimeSpan.FromMilliseconds(100)));

        // Speech then trailing silence routed through the session must end the active capture.
        session.RouteAudio(Loud());
        session.RouteAudio(Loud());
        session.RouteAudio(silent);
        session.RouteAudio(silent);

        (await capture.Completed).ShouldBe(CaptureOutcome.Ended);

        // After close, routing must not reach any capture (no throw, no effect).
        session.CloseCapture();
        Should.NotThrow(() => session.RouteAudio(Loud()));
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