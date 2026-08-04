# 07 — The remaining reads leave their stores alone

**What to build:** the same protection ticket 05 gives the sidebar, extended to the
three reads it did not cover — the agent list, the stream state and a topic's pending
approval. Each currently answers with an empty or null value when the client is
between connections, and each caller treats that as fact: the agent picker can empty
itself, and a stream resume can decide there is nothing to resume when it simply
could not ask.

All three answer or say **not live**, and their callers skip whatever they would have
done with the answer, leaving the store as it is. No toast: the user asked for none of
these.

The stream-state read is the one with a subtlety. Its `null` already means something
real — there is no stream in progress — so the not-live case has to stay separate from
it rather than folding into the same early return.

**Blocked by:** 03, 04.

**Status:** done

- [x] The agent list, stream state and pending approval calls answer with a result.
- [x] A populated agent list survives a fetch that could not be made.
- [x] A stream resume that cannot ask for the stream state does nothing and leaves
      the streaming state untouched, distinct from a read that answers "no stream".
- [x] An approval prompt already on screen is not dismissed by a pending-approval read
      that could not be made.
- [x] None of the three raises a toast.
- [x] When the transport is live, all three behave exactly as they do today.
