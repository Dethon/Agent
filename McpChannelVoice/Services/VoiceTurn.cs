namespace McpChannelVoice.Services;

// One user turn's reply state: how much of the answer has been queued, how much of it has been
// spoken, and whether the agent has stopped sending. The rule that ends a turn — the agent stopped
// sending AND every segment it produced has finished — lives here and has no route in from outside,
// so a new call site cannot settle a turn by some other means.
//
// The reply is streamed as several sentence jobs, so "the answer finished" is not "a job drained".
// Settling on the first drain ends FollowUpConversation, which chimes and reopens the mic while the
// remaining sentences are still being spoken.
public sealed class VoiceTurn
{
    private const long DispatchNotMarked = long.MinValue;

    private readonly Lock _gate = new();
    private TaskCompletionSource<bool> _spoken = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _dispatchedAt = DispatchNotMarked;
    private int _preambleClaimed;
    private int _segmentsStarted;
    private int _segmentsOutstanding;
    private int _streamComplete;
    private int _audioPlayed;
    private long _epoch;

    // Which minimum length the next segment must clear: the answer's opening clears a deliberately
    // low bar (it is the wait everyone feels), later sentences need more text because the audio
    // already playing is covering them.
    public bool NextSegmentIsFirst => Volatile.Read(ref _segmentsStarted) == 0;

    // Callers must Reset before the reply path can settle the new turn; otherwise a settle lands on
    // the discarded TCS and the awaiter blocks forever.
    public void Reset()
    {
        // All under _gate, which BeginSegment also takes: registering a segment and starting a turn
        // must not interleave, or a segment lands on the new turn's counter while its token still
        // carries the old epoch and is rejected — leaving the new turn permanently outstanding.
        lock (_gate)
        {
            Interlocked.Exchange(ref _preambleClaimed, 0);
            Interlocked.Exchange(ref _segmentsStarted, 0);
            Interlocked.Exchange(ref _segmentsOutstanding, 0);
            Interlocked.Exchange(ref _streamComplete, 0);
            Interlocked.Exchange(ref _audioPlayed, 0);
            Interlocked.Exchange(ref _dispatchedAt, DispatchNotMarked);
            Interlocked.Increment(ref _epoch);
            _spoken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // The returned token closes over the epoch the segment was registered under, so its release
    // reaches the same turn that counted it. Reading the epoch separately would leave a window for
    // Reset to land in between, which registers on one turn and releases against another.
    public SegmentToken BeginSegment()
    {
        lock (_gate)
        {
            var isFirst = _segmentsStarted == 0;
            Interlocked.Increment(ref _segmentsStarted);
            Interlocked.Increment(ref _segmentsOutstanding);
            return new SegmentToken(this, Interlocked.Read(ref _epoch), isFirst);
        }
    }

    // The agent has stopped sending. A turn that produced no audio at all settles here; one that did
    // settles as its last segment drains. The two halves of that decision are both in here because
    // nothing outside can see both.
    public void EndStream()
    {
        bool producedNothing;
        lock (_gate)
        {
            producedNothing = _segmentsStarted == 0;
            if (!producedNothing)
            {
                Interlocked.Exchange(ref _streamComplete, 1);
            }
        }

        if (!producedNothing)
        {
            SettleIfComplete();
            return;
        }

        // Nothing reached playback, so nothing consumed the dispatch stamp. Left behind it outlives
        // the turn, and a schedule firing into this same live session would consume it and report the
        // old turn's age as its own round trip.
        _ = TryConsumeDispatchedAt();
        Signal(spoken: false);
    }

    public Task<bool> AwaitSpoken()
    {
        lock (_gate)
        {
            return _spoken.Task;
        }
    }

    // Claimed by the first tool call of a turn, which speaks whatever the model said before it
    // ("Buscando") instead of leaving it buffered until the answer. One claim per turn: later tool
    // calls keep mid-run narration buffered so it cannot race the answer into the playback queue.
    public bool TryClaimPreamble() => Interlocked.CompareExchange(ref _preambleClaimed, 1, 0) == 0;

    // Stamped when a transcript actually reached the agent, so the hub can measure the agent round
    // trip it cannot otherwise see into (the agent's own MemoryRecall/LlmTotal stages live in a
    // different process). Single-use: a live session's conversation can also receive a schedule-fired
    // or agent-initiated reply that never went through a transcript dispatch, so a stamp left over
    // from an earlier real turn must not be readable by that later, unrelated reply — it would report
    // an invented, stale round trip.
    public void MarkDispatched(long timestamp) => Interlocked.Exchange(ref _dispatchedAt, timestamp);

    public long? TryConsumeDispatchedAt()
    {
        var stamp = Interlocked.Exchange(ref _dispatchedAt, DispatchNotMarked);
        return stamp == DispatchNotMarked ? null : stamp;
    }

    // Playback callbacks outlive the turn that queued them (a preempted or slow job can drain after
    // FollowUpConversation has moved on), and this handshake is counter-based rather than an
    // idempotent "set the TCS" — so a stale decrement would drive the NEXT turn's outstanding count
    // negative and it could then never reach zero, wedging the mic until ReplyTimeoutMs. A token
    // carries the epoch it was issued under and is ignored once that epoch moves.
    internal void CompleteSegment(long epoch)
    {
        // Epoch check and decrement under the gate Reset takes: checked then decremented without it,
        // a reset landing between the two puts the stale decrement on the NEW turn's counter, which
        // then can never reach zero. SettleIfComplete stays outside — after a reset it reads the
        // zeroed flags and no-ops, and it re-enters _gate to signal.
        lock (_gate)
        {
            if (epoch != Interlocked.Read(ref _epoch))
            {
                return;
            }
            Interlocked.Exchange(ref _audioPlayed, 1);
            Interlocked.Decrement(ref _segmentsOutstanding);
        }
        SettleIfComplete();
    }

    // A segment that never plays (synthesis threw, or the queue refused it) must NOT settle the turn
    // on its own: sentences behind it may still be queued, and settling here would end
    // FollowUpConversation, whose chime is a High-priority job — it would preempt the sentence
    // currently playing and the rest would then be spoken into an open capture.
    internal void FailSegment(long epoch)
    {
        // Same gate discipline as CompleteSegment, for the same reason.
        lock (_gate)
        {
            if (epoch != Interlocked.Read(ref _epoch))
            {
                return;
            }
            Interlocked.Decrement(ref _segmentsOutstanding);
        }
        SettleIfComplete();
    }

    // Spoken when any segment reached the satellite — half an answer still played, and the user is
    // owed the follow-up window; Silent when every one of them failed.
    private void SettleIfComplete()
    {
        if (Volatile.Read(ref _streamComplete) != 1
            || Volatile.Read(ref _segmentsOutstanding) != 0
            || Volatile.Read(ref _segmentsStarted) == 0)
        {
            return;
        }

        Signal(Volatile.Read(ref _audioPlayed) == 1);
    }

    private void Signal(bool spoken)
    {
        lock (_gate)
        {
            _spoken.TrySetResult(spoken);
        }
    }
}

// One reply segment's claim on the turn that counted it. Completing or failing it is a method here
// rather than a call carrying an epoch by hand, so releasing a segment against a turn it never
// registered on is unrepresentable.
public readonly struct SegmentToken(VoiceTurn? turn, long epoch, bool isFirst)
{
    // Only the turn's FIRST reply segment may publish the time-to-first-audio spans, or a
    // three-sentence answer reports three samples of a metric that means "how long until the user
    // heard anything" and the turn decomposition stops summing.
    public bool IsFirst => isFirst;

    // A default token belongs to no turn — it is what a job that never registered a segment (the
    // preamble flush) carries — so releasing it is a no-op rather than a null reference.
    public void Complete() => turn?.CompleteSegment(epoch);

    public void Fail() => turn?.FailSegment(epoch);
}