# 05 — Entry points for topic selection and deletion

**What to build:** selecting a topic and deleting one become awaitable and testable. Both effects already carry their work in a private async method behind a handler registration, so the change is making that method public, passing the action's payload as parameters rather than the action record, and attaching the fault logging from ticket 01 in the wrapper.

Each effect gets its own test file. Selecting a topic marks it read and resumes any stream it had; deleting one removes it from the server and clears its messages. Neither behaviour is pinned today.

The diffs should be nothing but the signature and the fault log. If converting either effect changes what it does, that is a behaviour change and needs explaining rather than committing.

The topic-deletion effect is also rewritten by `webchat-slice-shape` tickets 05 and 06, which change the body of its handler rather than its signature. Whichever of the two lands second rebases onto the first. This is not a blocking dependency.

**Blocked by:** 01 (fault logging).

**Status:** done

- [x] Topic-selection work is reachable by calling a public method with a topic id and awaiting it.
- [x] Topic-deletion work is reachable by calling a public method and awaiting it.
- [x] Dispatching either action still runs the same work.
- [x] A fault in either effect is logged rather than discarded.
- [x] Selecting a topic marks it as read when it has unread messages, and does not write when it does not.
- [x] Deleting a topic removes it from the server and clears its messages locally.
- [x] Both effects have a test file that dispatches the action in at least one case, so the registration itself stays covered.
- [x] No production behaviour changes beyond the fault logging.
