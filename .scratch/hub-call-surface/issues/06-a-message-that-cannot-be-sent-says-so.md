# 06 — A message that cannot be sent says so

**What to build:** a user who types a message during an interruption is told it did
not send. Today it disappears — no bubble, no error, nothing to retry. The session
start comes back `false`, the effect returns silently, and the message is gone.

The second half of the same defect is worse. When a reply is already streaming, the
send goes through the enqueue call instead, and a `false` from that means "there is
no active stream to enqueue onto" — so the client opens a *new* stream, announces it,
and the stream produces nothing. The user watches a reply that has already failed.

The session start, the send stream and the enqueue call answer or say **not live**.
The send path raises one error toast when the answer could not be made, and the
send-or-enqueue path stops treating a not-live enqueue as "no active stream": it must
distinguish the server saying no from never having asked.

The streaming calls answer before iteration rather than by iterating nothing, so the
caller knows a stream will not start before it announces one.

**Blocked by:** 01, 03, 04.

**Status:** done

- [x] The session start, send stream and enqueue calls answer with a result.
- [x] The streaming calls report not live before any iteration begins, rather than
      yielding an empty sequence.
- [x] A message typed while the transport cannot carry a call raises exactly one
      toast and adds no message to the transcript.
- [x] A not-live enqueue does not fall through to opening a new stream and announces
      no stream.
- [x] A server answering no to the enqueue still opens a new stream, as it does
      today — the two outcomes stay distinct.
- [x] When the transport is live, sending, enqueuing and starting a session behave
      exactly as they do today.
- [x] The existing send-message and streaming suites pass, adjusted only for the
      changed signatures.
