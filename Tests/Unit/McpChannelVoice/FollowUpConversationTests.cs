using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class FollowUpConversationTests
{
    private static SilenceGate AnyGate(bool followUp) => new(
        500, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(5000),
        TimeSpan.FromMilliseconds(100),
        noSpeechTimeout: followUp ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero);

    private sealed class Harness
    {
        public readonly List<string> Events = [];
        public readonly FakeTimeProvider Time = new(DateTimeOffset.UtcNow);
        public readonly List<UtteranceCapture> Opened = [];
        private TaskCompletionSource<bool> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FollowUpConversation Build(FollowUpSettings followUp) => new(
            followUp,
            Time)
        {
            OpenCapture = isFollowUp =>
            {
                var c = new UtteranceCapture(AnyGate(isFollowUp));
                Opened.Add(c);
                Events.Add(isFollowUp ? "open-followup" : "open-first");
                return c;
            },
            CloseCapture = () => { },
            TranscribeAndDispatch = (_, isFollowUp, _) =>
            {
                Events.Add(isFollowUp ? "dispatch-followup" : "dispatch-first");
                return Task.CompletedTask;
            },
            EnqueueChime = _ => { Events.Add("chime"); return Task.CompletedTask; },
            EndConversation = _ => { Events.Add("end"); return Task.CompletedTask; },
            ResetTurn = () => _reply = new(TaskCreationOptions.RunContinuationsAsynchronously),
            AwaitReply = () => _reply.Task,
            OnFollowUpWindow = _ => Task.CompletedTask
        };

        public void Reply(bool spoke) => _reply.TrySetResult(spoke);
    }

    [Fact]
    public async Task Disabled_DispatchesThenEndsImmediately_NoReplyWait()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = false });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd(); // utterance ended (speech)

        await Task.Delay(50);
        h.Events.ShouldBe(["open-first", "dispatch-first", "end"]);

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SpeechReplyThenFollowUp_OpensSecondWindowWithoutWake()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = true, PlaybackTailMs = 400, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();          // first utterance ends
        await Task.Delay(50);
        h.Reply(spoke: true);            // agent reply spoken
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromMilliseconds(400)); // tail
        await Task.Delay(50);

        h.Events.ShouldContain("chime");
        h.Events.ShouldContain("open-followup");

        // The follow-up window opened a second capture without a new wake.
        h.Opened.Count.ShouldBe(2);

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_FollowUpSilence_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 500 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();
        await Task.Delay(50);
        h.Reply(spoke: true);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        // Second capture is the follow-up window; feeding only silence => NoSpeech => end.
        var followUp = h.Opened[1];
        var silent = new AudioChunk { Data = new byte[3200], Format = AudioFormat.WyomingStandard };
        for (var i = 0; i < 6; i++)
        { followUp.Feed(silent); }

        await Task.Delay(50);
        h.Events.ShouldContain("end");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SilentTurn_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();
        await Task.Delay(50);
        h.Reply(spoke: false); // agent produced no audio

        await Task.Delay(50);
        h.Events.ShouldContain("end");
        h.Events.ShouldNotContain("chime");

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_SecondWakeAfterConversationEnds_StartsFreshConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true });
        var run = sut.RunAsync(CancellationToken.None);

        // Conversation 1: wake, speak, agent stays silent -> ends.
        sut.OnWake();
        h.Opened[0].ForceEnd();
        await Task.Delay(50);
        h.Reply(spoke: false);
        await Task.Delay(50);

        // Conversation 2: a brand-new wake must start a fresh first-utterance.
        sut.OnWake();
        h.Opened[1].ForceEnd();
        await Task.Delay(50);

        h.Events.Count(e => e == "open-first").ShouldBe(2);
        h.Events.Count(e => e == "dispatch-first").ShouldBe(2);

        await StopAsync(sut, run);
    }

    [Fact]
    public async Task Enabled_MaxTurnsReached_EndsConversation()
    {
        var h = new Harness();
        var sut = h.Build(new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, MaxTurns = 1 });
        var run = sut.RunAsync(CancellationToken.None);

        sut.OnWake();
        h.Opened[0].ForceEnd();          // first utterance
        await Task.Delay(50);
        h.Reply(spoke: true);            // turns=0 < 1 -> opens ONE follow-up window
        await Task.Delay(50);
        // (tail is 0; advance the fake clock in case Task.Delay(0, fakeTime) needs a tick)
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        h.Opened.Count.ShouldBe(2);      // one follow-up window opened
        h.Opened[1].ForceEnd();          // follow-up utterance
        await Task.Delay(50);
        h.Reply(spoke: true);            // turns=1 >= MaxTurns=1 -> end
        await Task.Delay(50);

        h.Events.ShouldContain("end");
        h.Events.Count(e => e == "open-followup").ShouldBe(1); // capped at one
        await StopAsync(sut, run);
    }

    private static async Task StopAsync(FollowUpConversation sut, Task run)
    {
        sut.Dispose();
        try
        { await run.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* unwinds on dispose/cancel */ }
    }
}