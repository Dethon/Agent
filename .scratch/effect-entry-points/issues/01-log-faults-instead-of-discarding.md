# 01 — Log effect faults instead of discarding them

**What to build:** a task started by an effect and then abandoned leaves a log entry when it fails. Today thirteen sites across the effects assign the task to a discard, so a throw inside first-load initialization, topic history loading or agent switching produces no log line, no toast and no retry. The app is left in a half-finished state and the only evidence is in the browser's network tab.

Add one `Task` extension that takes a logger and attaches a continuation running only on faulted. It returns nothing, because every caller is abandoning the task by construction. Nothing switches over to it in this ticket — the effects adopt it as they convert, in tickets 03 through 07. This is the expand half.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A task that faults produces a log entry naming the exception.
- [x] A task that completes normally produces no log entry.
- [x] A task that is already faulted when the helper is called still produces a log entry.
- [x] The helper does not throw and does not block the calling thread.
- [x] The behaviour is covered by a new unit test file written before the helper exists.
- [x] No existing effect changes in this ticket.
