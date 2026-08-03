using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

// The per-turn handshake is what stops FollowUpConversation re-arming the mic mid-answer. With the
// reply streamed as several sentence jobs, "the reply finished" is no longer "a job drained" — it is
// "every started segment finished AND the agent's stream ended". These pin that.
//
// Latency decomposition is a different question and lives in TurnLatencyDecompositionTests.
public class VoiceTurnTests
{
    private static VoiceTurn Started()
    {
        var turn = new VoiceTurn();
        turn.Reset();
        return turn;
    }

    [Fact]
    public async Task SingleSegment_DrainThenEndStream_SettlesSpoken()
    {
        var turn = Started();
        var spoken = turn.AwaitSpoken();

        turn.BeginSegment().Complete();
        spoken.IsCompleted.ShouldBeFalse(); // the agent may still send more text

        turn.EndStream();
        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeTrue();
    }

    [Fact]
    public void SingleSegment_EndStreamThenDrain_SettlesSpoken()
    {
        var turn = Started();
        var spoken = turn.AwaitSpoken();

        var segment = turn.BeginSegment();
        turn.EndStream();
        spoken.IsCompleted.ShouldBeFalse(); // audio is still playing

        segment.Complete();
        spoken.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task MultipleSegments_FirstDrain_DoesNotSettleTheTurn()
    {
        // Settling on the first segment's drain ends FollowUpConversation, which plays the chime
        // and reopens the mic while sentences 2..N are still being spoken.
        var turn = Started();
        var spoken = turn.AwaitSpoken();

        var one = turn.BeginSegment();
        var two = turn.BeginSegment();
        var three = turn.BeginSegment();

        one.Complete();
        turn.EndStream();
        two.Complete();
        spoken.IsCompleted.ShouldBeFalse();

        three.Complete();
        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeTrue();
    }

    [Fact]
    public async Task FailedSegment_WithAnotherOutstanding_DoesNotSettleTheTurn()
    {
        // A synthesis error on sentence 3 of 4 must not end the turn: the chime that follows is a
        // High-priority job, so it would preempt whatever is playing and the remaining sentence
        // would then be spoken into an open capture on a satellite with no echo cancellation.
        var turn = Started();
        var spoken = turn.AwaitSpoken();

        turn.BeginSegment().Complete();
        var failing = turn.BeginSegment();
        var stillPlaying = turn.BeginSegment();

        failing.Fail();
        spoken.IsCompleted.ShouldBeFalse();

        turn.EndStream();
        spoken.IsCompleted.ShouldBeFalse(); // one segment is still playing

        stillPlaying.Complete();
        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeTrue();
    }

    [Fact]
    public async Task Token_FromAPreviousTurn_IsIgnored()
    {
        // Playback callbacks outlive their turn (a preempted job drains late). With a counter-based
        // handshake a stale decrement would drive the new turn negative, so it could never reach
        // zero and the mic would stay wedged until the ~120s ReplyTimeoutMs.
        var turn = Started();
        var stale = turn.BeginSegment();

        turn.Reset();
        var spoken = turn.AwaitSpoken();

        stale.Complete(); // late callbacks from the finished turn
        stale.Fail();

        var current = turn.BeginSegment();
        turn.EndStream();
        current.Complete();

        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeTrue();
    }

    [Fact]
    public async Task Reset_BetweenBeginAndComplete_DoesNotDriveTheNewTurnNegative()
    {
        // The new turn owes exactly one release, from its own segment. If the stale completion were
        // counted against it the outstanding count would sit at -1, never reach zero again, and the
        // turn would only end on the reply timeout.
        var turn = Started();
        var stale = turn.BeginSegment();

        turn.Reset();
        var spoken = turn.AwaitSpoken();
        var current = turn.BeginSegment();

        stale.Complete();
        turn.EndStream();
        spoken.IsCompleted.ShouldBeFalse(); // the new turn's own segment is still outstanding

        current.Complete();
        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeTrue();
    }

    [Fact]
    public async Task EndStream_WithNoSegmentsStarted_SettlesSilent()
    {
        // An empty answer starts no segments. Waiting for one would cost the user the full reply
        // timeout for something that was never coming.
        var turn = Started();
        var spoken = turn.AwaitSpoken();

        turn.EndStream();

        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeFalse();
    }

    [Fact]
    public void EndStream_WithNoSegmentsStarted_ReleasesTheDispatchStamp()
    {
        // Nothing reached playback, so nothing consumed the stamp. Left behind it outlives the turn,
        // and a schedule firing into this same live session would report the old turn's age as its
        // own round trip.
        var turn = Started();
        turn.MarkDispatched(1234);

        turn.EndStream();

        turn.TryConsumeDispatchedAt().ShouldBeNull();
    }

    [Fact]
    public async Task EverySegmentFailed_SettlesSilent()
    {
        var turn = Started();
        var spoken = turn.AwaitSpoken();

        turn.BeginSegment().Fail();
        turn.EndStream();

        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeFalse();
    }

    [Fact]
    public async Task FailAfterSomeAudioPlayed_SettlesSpoken()
    {
        // Half an answer reached the satellite, so the turn did speak; ending it Silent would be a
        // lie and would also skip the follow-up window the user is owed.
        var turn = Started();
        var spoken = turn.AwaitSpoken();

        turn.BeginSegment().Complete();
        turn.BeginSegment().Fail();
        turn.EndStream();

        spoken.IsCompleted.ShouldBeTrue();
        (await spoken).ShouldBeTrue();
    }

    [Fact]
    public void NextSegmentIsFirst_IsTrueOnlyBeforeTheFirstSegment()
    {
        // Two different questions used to be answered from one public counter. This one picks the
        // minimum length: the answer's opening clears a low bar, later sentences need more text.
        var turn = Started();
        turn.NextSegmentIsFirst.ShouldBeTrue();

        turn.BeginSegment().Complete();
        turn.NextSegmentIsFirst.ShouldBeFalse();

        turn.BeginSegment();
        turn.NextSegmentIsFirst.ShouldBeFalse(); // counts starts, not what is still outstanding
    }

    [Fact]
    public void Token_IsFirst_MarksOnlyTheTurnsFirstSegment()
    {
        // The other question: which segment may publish the time-to-first-audio spans. A
        // three-sentence answer must report one sample, not three.
        var turn = Started();

        turn.BeginSegment().IsFirst.ShouldBeTrue();
        turn.BeginSegment().IsFirst.ShouldBeFalse();

        turn.Reset();
        turn.BeginSegment().IsFirst.ShouldBeTrue();
    }

    [Fact]
    public void Reset_ClearsSegmentState()
    {
        var turn = Started();
        var first = turn.BeginSegment();
        turn.EndStream();
        first.Complete();

        turn.Reset();

        turn.NextSegmentIsFirst.ShouldBeTrue();
        var spoken = turn.AwaitSpoken();
        turn.BeginSegment().Complete();
        spoken.IsCompleted.ShouldBeFalse(); // the previous turn's stream-complete must not carry over
    }

    [Fact]
    public void Reset_ClearsThePreambleClaim()
    {
        var turn = Started();
        turn.TryClaimPreamble().ShouldBeTrue();
        turn.TryClaimPreamble().ShouldBeFalse();

        turn.Reset();

        turn.TryClaimPreamble().ShouldBeTrue();
    }

    [Fact]
    public void Reset_ClearsTheDispatchStamp()
    {
        var turn = Started();
        turn.MarkDispatched(1234);

        turn.Reset();

        turn.TryConsumeDispatchedAt().ShouldBeNull();
    }

    [Fact]
    public void TryConsumeDispatchedAt_IsSingleUse()
    {
        var turn = Started();
        turn.MarkDispatched(1234);

        turn.TryConsumeDispatchedAt().ShouldBe(1234);
        turn.TryConsumeDispatchedAt().ShouldBeNull();
    }
}