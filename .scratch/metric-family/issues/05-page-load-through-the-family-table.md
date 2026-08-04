# 05 — Page load through the family table

**What to build:** Opening the dashboard loads every metric family through that family's
own definition, so the page-load path and the live-update path can no longer disagree
about how a family's request is built.

The page-load effect is a second hand-written fan-out over the same seven families: it
takes eleven stores, sets the date range on each, issues a pair of calls per family, and
assigns a pair of results per family. It is where the voice aggregation bug had its
second home, and it is the reason a new family means edits in two effects rather than
one.

Wire the event-loading half of the family, declared in ticket 04, and let the page-load
effect become what is genuinely not per-family: setting the date range across the table,
starting both operations for each family, and the summary and health calls. Memory keeps
its three event sources inside its own load operation.

Everything still runs in parallel, exactly as it does today. A failure anywhere in the
load still reports the dashboard disconnected — that catch stays where it is, with the
caller that has a policy, and it still swallows the reason as it does now.

**Blocked by:** 04 — the family table and its event-loading operation are declared there.

**Status:** done

- [x] The page-load effect no longer names any individual family.
- [x] Loading sets the date range on every family and issues both that family's event request and its breakdown request.
- [x] Memory still loads its three event sources.
- [x] Requests still go out in parallel; a page load is no slower than it is today.
- [x] A failure anywhere in the load still turns the connection indicator red, and that rule is written in one place.
- [x] The summary and health calls are unchanged.
- [x] A test asserts that a load sets the date range on every family and issues both requests for each, driven through the existing client seam.
- [x] The existing page-load regression test for the voice aggregation still passes.
- [x] Adding a family now means one entry in the table and no edit to either effect.
