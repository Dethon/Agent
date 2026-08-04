# 08 — The remaining user actions say so

**What to build:** the four things a user can do to a conversation, other than send a
message, stop failing silently. Renaming a conversation, deleting one, cancelling a
running reply, and answering an approval prompt all currently do nothing at all when
the client is between connections — the call is skipped and the interface carries on
as if it had happened. A user who deletes a conversation during an interruption
watches it disappear from the sidebar and come back on the next reload.

Each call answers or says **not live**, and each caller raises one error toast when
it could not be made. The toast store already suppresses a repeat of the same
message, so several failures in one window are one toast on screen.

The approval response is the one worth care: it is boolean-valued, and the server
answering `false` — this approval is no longer pending — must stay distinct from
never having asked.

**Blocked by:** 01, 03, 04.

**Status:** done

- [x] The save, delete, cancel and approval-response calls answer with a result.
- [x] Each raises one toast when the call could not be made.
- [x] A delete that could not be made leaves the conversation in the sidebar rather
      than removing it optimistically.
- [x] An approval answered while the transport cannot carry the call leaves the
      prompt on screen so it can be answered again.
- [x] A server answering no to the approval response stays distinct from not live and
      keeps today's behaviour.
- [x] Two failed user actions in the same window produce one toast, not two.
- [x] When the transport is live, all four behave exactly as they do today.
