# 01 — The reduction enum becomes an aggregation

**What to build:** A developer reading a metrics signature can tell whether a parameter
chooses a quantity or a way of reducing one. Today one enum names a reduction over a set
of durations — average, percentiles, maximum, count — and it is called a latency metric.
On the latency page it is the user's *metric* pill. On the voice page the same enum is
the *aggregate* pill, sitting beside a different enum that is also called a metric but
actually names which kind of event happened. Three concepts, two names.

Rename the reduction enum to say what it is: an aggregation. The member names do not
change, so query strings, stored preferences and every value already saved in a
browser keep working. The query string parameter names do not change either.

The voice event-kind enum keeps its name. It is persisted by integer value in Redis and
its members are pinned, and renaming it would reach into the voice channel server.

This is a prefactor. Every later ticket in this feature edits the same files, and doing
the rename first means none of them carries it. Behaviour is identical everywhere.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The reduction enum is named for what it is; its members are unchanged.
- [x] Every reference across the domain, the observability service, the dashboard client and the tests compiles against the new name.
- [x] The latency page's metric pill and the voice page's aggregate pill are typed as the same thing, and both pages behave exactly as before.
- [x] Query string parameter names are unchanged, so no request or route moves.
- [x] A preference saved under the old build still loads and still selects the same pill.
- [x] `CONTEXT.md`'s aggregation entry matches what the code now calls it.
- [x] The existing dashboard and observability test suites pass with no change beyond the rename.
