using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The per-turn handshake is what stops FollowUpConversation re-arming the mic mid-answer. With the
// reply streamed as several sentence jobs, "the reply finished" is no longer "a job drained" — it is
// "every started segment drained AND the agent's stream ended". These pin that.
public class SatelliteSessionReplySegmentTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public void SingleSegment_DrainThenStreamComplete_SettlesSpoken()
    {
        var session = MakeSession();
        session.ResetTurn();
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.CompleteReplySegment();
        turn.IsCompleted.ShouldBeFalse(); // the agent may still send more text

        session.MarkReplyStreamComplete();
        turn.IsCompleted.ShouldBeTrue();
        turn.Result.ShouldBeTrue();
    }

    [Fact]
    public void SingleSegment_StreamCompleteThenDrain_SettlesSpoken()
    {
        var session = MakeSession();
        session.ResetTurn();
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.MarkReplyStreamComplete();
        turn.IsCompleted.ShouldBeFalse(); // audio is still playing

        session.CompleteReplySegment();
        turn.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void MultipleSegments_FirstDrain_DoesNotSettleTheTurn()
    {
        // The regression this whole class exists for: signalling on the first segment's drain ends
        // FollowUpConversation, which plays the chime and reopens the mic while sentences 2..N are
        // still being spoken.
        var session = MakeSession();
        session.ResetTurn();
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.BeginReplySegment();
        session.BeginReplySegment();

        session.CompleteReplySegment();
        session.MarkReplyStreamComplete();
        session.CompleteReplySegment();
        turn.IsCompleted.ShouldBeFalse();

        session.CompleteReplySegment();
        turn.IsCompleted.ShouldBeTrue();
        turn.Result.ShouldBeTrue();
    }

    [Fact]
    public void MarkReplyStreamComplete_WithNoSegmentsStarted_LeavesTheTurnUnsettled()
    {
        // An empty answer starts no segments; the caller signals Silent explicitly rather than
        // having this settle Spoken for audio that never played.
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
        // SendReplyTool uses this to pick the first-segment character threshold, so it must keep
        // counting up as segments drain rather than tracking outstanding work.
        var session = MakeSession();
        session.ResetTurn();

        session.BeginReplySegment();
        session.CompleteReplySegment();
        session.BeginReplySegment();

        session.ReplySegmentsStarted.ShouldBe(2);
    }

    [Fact]
    public void ResetTurn_ClearsSegmentState()
    {
        var session = MakeSession();
        session.ResetTurn();
        session.BeginReplySegment();
        session.MarkReplyStreamComplete();
        session.CompleteReplySegment();

        session.ResetTurn();

        session.ReplySegmentsStarted.ShouldBe(0);
        var turn = session.WaitForTurnSpokenAsync();
        session.BeginReplySegment();
        session.CompleteReplySegment();
        turn.IsCompleted.ShouldBeFalse(); // the previous turn's stream-complete must not carry over
    }

    [Fact]
    public void FailReplySegment_AfterAudioPlayed_SettlesSpoken()
    {
        // Half an answer reached the satellite, so the turn did speak; ending it Silent would be a
        // lie and would also skip the follow-up window the user is owed.
        var session = MakeSession();
        session.ResetTurn();
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.CompleteReplySegment();
        session.BeginReplySegment();
        session.FailReplySegment();

        turn.IsCompleted.ShouldBeTrue();
        turn.Result.ShouldBeTrue();
    }

    [Fact]
    public void FailReplySegment_BeforeAnyAudio_SettlesSilent()
    {
        var session = MakeSession();
        session.ResetTurn();
        var turn = session.WaitForTurnSpokenAsync();

        session.BeginReplySegment();
        session.FailReplySegment();

        turn.IsCompleted.ShouldBeTrue();
        turn.Result.ShouldBeFalse();
    }
}