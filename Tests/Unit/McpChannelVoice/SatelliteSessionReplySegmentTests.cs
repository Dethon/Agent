using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The per-turn handshake is what stops FollowUpConversation re-arming the mic mid-answer. With the
// reply streamed as several sentence jobs, "the reply finished" is no longer "a job drained" — it is
// "every started segment finished AND the agent's stream ended". These pin that.
public class SatelliteSessionReplySegmentTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public async Task SingleSegment_DrainThenStreamComplete_SettlesSpoken()
    {
        var session = MakeSession();
        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.CompleteReplySegment(epoch);
        turn.IsCompleted.ShouldBeFalse(); // the agent may still send more text

        session.MarkReplyStreamComplete();
        turn.IsCompleted.ShouldBeTrue();
        (await turn).ShouldBeTrue();
    }

    [Fact]
    public void SingleSegment_StreamCompleteThenDrain_SettlesSpoken()
    {
        var session = MakeSession();
        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.MarkReplyStreamComplete();
        turn.IsCompleted.ShouldBeFalse(); // audio is still playing

        session.CompleteReplySegment(epoch);
        turn.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task MultipleSegments_FirstDrain_DoesNotSettleTheTurn()
    {
        // Signalling on the first segment's drain ends FollowUpConversation, which plays the chime
        // and reopens the mic while sentences 2..N are still being spoken.
        var session = MakeSession();
        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.BeginReplySegment();
        session.BeginReplySegment();

        session.CompleteReplySegment(epoch);
        session.MarkReplyStreamComplete();
        session.CompleteReplySegment(epoch);
        turn.IsCompleted.ShouldBeFalse();

        session.CompleteReplySegment(epoch);
        turn.IsCompleted.ShouldBeTrue();
        (await turn).ShouldBeTrue();
    }

    [Fact]
    public async Task FailReplySegment_WithAnotherSegmentOutstanding_DoesNotSettleTheTurn()
    {
        // A synthesis error on sentence 3 of 4 must not end the turn: the chime that follows is a
        // High-priority job, so it would preempt whatever is playing and the remaining sentence
        // would then be spoken into an open capture on a satellite with no echo cancellation.
        var session = MakeSession();
        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.CompleteReplySegment(epoch);
        session.BeginReplySegment();
        session.BeginReplySegment();

        session.FailReplySegment(epoch);
        turn.IsCompleted.ShouldBeFalse();

        session.MarkReplyStreamComplete();
        turn.IsCompleted.ShouldBeFalse(); // one segment is still playing

        session.CompleteReplySegment(epoch);
        turn.IsCompleted.ShouldBeTrue();
        (await turn).ShouldBeTrue();
    }

    [Fact]
    public async Task CompleteReplySegment_FromAPreviousTurn_IsIgnored()
    {
        // Playback callbacks outlive their turn (a preempted job drains late). With a counter-based
        // handshake a stale decrement would drive the new turn negative, so it could never reach
        // zero and the mic would stay wedged until the ~120s ReplyTimeoutMs.
        var session = MakeSession();
        session.ResetTurn();
        var staleEpoch = session.CurrentTurnEpoch;
        session.BeginReplySegment();

        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;
        var turn = session.WaitForTurnSpokenAsync();

        session.CompleteReplySegment(staleEpoch); // late callback from the finished turn
        session.FailReplySegment(staleEpoch);

        session.BeginReplySegment();
        session.MarkReplyStreamComplete();
        session.CompleteReplySegment(epoch);

        turn.IsCompleted.ShouldBeTrue();
        (await turn).ShouldBeTrue();
    }

    [Fact]
    public void MarkReplyStreamComplete_WithNoSegmentsStarted_LeavesTheTurnUnsettled()
    {
        // An empty answer starts no segments; the caller signals Silent explicitly rather than
        // having this settle for audio that never played.
        var session = MakeSession();
        session.ResetTurn();
        var turn = session.WaitForTurnSpokenAsync();

        session.MarkReplyStreamComplete();

        turn.IsCompleted.ShouldBeFalse();
        session.ReplySegmentsStarted.ShouldBe(0);
    }

    [Fact]
    public void ReplySegmentsStarted_CountsStartsNotOutstanding()
    {
        // SendReplyTool uses this both to pick the first-segment threshold and to decide which
        // segment may publish the time-to-first-audio spans, so it must keep counting up.
        var session = MakeSession();
        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;

        session.BeginReplySegment();
        session.CompleteReplySegment(epoch);
        session.BeginReplySegment();

        session.ReplySegmentsStarted.ShouldBe(2);
    }

    [Fact]
    public void ResetTurn_ClearsSegmentState()
    {
        var session = MakeSession();
        session.ResetTurn();
        var first = session.CurrentTurnEpoch;
        session.BeginReplySegment();
        session.MarkReplyStreamComplete();
        session.CompleteReplySegment(first);

        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;

        session.ReplySegmentsStarted.ShouldBe(0);
        var turn = session.WaitForTurnSpokenAsync();
        session.BeginReplySegment();
        session.CompleteReplySegment(epoch);
        turn.IsCompleted.ShouldBeFalse(); // the previous turn's stream-complete must not carry over
    }

    [Fact]
    public async Task EverySegmentFailed_SettlesSilent()
    {
        var session = MakeSession();
        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.FailReplySegment(epoch);
        session.MarkReplyStreamComplete();

        turn.IsCompleted.ShouldBeTrue();
        (await turn).ShouldBeFalse();
    }

    [Fact]
    public async Task FailAfterSomeAudioPlayed_SettlesSpoken()
    {
        // Half an answer reached the satellite, so the turn did speak; ending it Silent would be a
        // lie and would also skip the follow-up window the user is owed.
        var session = MakeSession();
        session.ResetTurn();
        var epoch = session.CurrentTurnEpoch;
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.CompleteReplySegment(epoch);
        session.BeginReplySegment();
        session.FailReplySegment(epoch);
        session.MarkReplyStreamComplete();

        turn.IsCompleted.ShouldBeTrue();
        (await turn).ShouldBeTrue();
    }

    [Fact]
    public void BeginReplySegment_ReturnsTheEpochItRegisteredUnder()
    {
        // Registration and release must agree on one epoch. Reading CurrentTurnEpoch separately
        // leaves a window where ResetTurn lands between the read and the increment: the segment is
        // then registered on the NEW turn while all of its callbacks are rejected as stale, and the
        // new turn can never reach zero outstanding.
        var session = MakeSession();
        session.ResetTurn();
        var turn = session.WaitForTurnSpokenAsync();

        var epoch = session.BeginReplySegment();
        epoch.ShouldBe(session.CurrentTurnEpoch);

        session.CompleteReplySegment(epoch);
        session.MarkReplyStreamComplete();
        turn.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void BeginReplySegment_AfterResetTurn_RegistersOnTheNewTurn()
    {
        var session = MakeSession();
        session.ResetTurn();
        var stale = session.BeginReplySegment();

        session.ResetTurn();
        var epoch = session.BeginReplySegment();

        epoch.ShouldNotBe(stale);
        session.ReplySegmentsStarted.ShouldBe(1);
    }

}