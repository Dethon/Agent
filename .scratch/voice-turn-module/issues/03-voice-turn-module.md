# 03 — VoiceTurn, the segment token, and the reply-tool collapse

**What to build:** A voice turn is over only once the agent has stopped sending and every segment it produced has finished playing. That rule currently lives in a private method with a comment, on an object that also has to publish its raw segment counter so the reply tool can answer three unrelated questions from it. Publishing the counter publishes the option of settling a turn some other way, so the rule is one caller away from being bypassed.

After this ticket one module owns the turn. It holds every field the turn reset touches — the started, outstanding, stream-complete and audio-played counters, the epoch, the preamble claim, the dispatch stamp and the lock — and its interface offers no route to the settle rule. The module hangs off the session as a property; the session does not forward methods to it, because a forwarding layer has the same surface as before.

Beginning a segment returns a token that closes over its own epoch and knows whether it is the turn's first. Completing or failing a segment is a method on that token. Registering a segment on one turn and releasing it against another stops being a comment and becomes unrepresentable.

The reply tool's three reads of the counter become three different things: the stream-complete branch becomes one call that ends the stream and lets the module decide silent versus spoken; the minimum-length choice asks whether the next segment is the first; the metrics decision asks the token. The counter then goes private.

The interface was settled in the plan and is repeated here because it encodes the decisions:

```csharp
public sealed class VoiceTurn
{
    public SegmentToken BeginSegment();       // token carries epoch + IsFirst
    public void EndStream();                  // silent-vs-complete decided inside
    public Task<bool> AwaitSpoken();
    public void Reset();
    public bool NextSegmentIsFirst { get; }
    public void MarkDispatched(long timestamp);
    public long? TryConsumeDispatchedAt();
    public bool TryClaimPreamble();
}

public readonly struct SegmentToken
{
    public bool IsFirst { get; }
    public void Complete();
    public void Fail();
}
```

`MarkDispatched` is on this list although the plan's version omitted it: the dispatch stamp is cleared by the turn reset, so it has to move with everything else the reset touches.

There are no public signal-spoken or signal-silent methods. Tests that need a settled turn drive the real path — begin a segment, complete it, end the stream.

This is one ticket rather than three because splitting it leaves the session holding the module and the old methods at the same time, which is exactly the split invariant this work removes.

The turn-start and speech-end latency anchors stay on the session. They are not part of the turn reset, and the playback loop reads them back with the same time provider that stamped them.

**Blocked by:** None — can start immediately. Runs in parallel with 01 and 02.

**Status:** done

- [x] The module owns every field the turn reset touches; none of them is reachable from outside it.
- [x] A stream that ends with no segments started settles the turn silent, and releases the dispatch stamp so it cannot outlive the turn.
- [x] A stream that ends with one segment drained and another still outstanding does not settle the turn.
- [x] A completion or failure callback carrying a previous turn's epoch is ignored.
- [x] A reset landing between a segment beginning and its completion does not drive the new turn's counter negative.
- [x] A turn where every segment failed settles silent; a turn where any segment played settles spoken.
- [x] The reply tool no longer reads a segment counter: it ends the stream in one call, asks whether the next segment is the first for the minimum-length choice, and asks the token for the metrics decision.
- [x] The existing reply-segment unit file is retargeted at the module and renamed. Pick a name that is told apart at a glance from the existing file testing turn latency decomposition.
- [x] The two handshake tests currently in the playback unit file move to the module's file — they test the handshake, not playback.
- [x] The reply tool's own unit file passes with the epoch parameters gone from its call sites.
- [x] The integration files get the minimal edit needed to stay green; restructuring them is ticket 05.
- [x] Every hazard comment moves with the code it describes. Each one records a bug that actually happened; deleting one is a regression.
