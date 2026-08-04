# 07 — The remaining six pages

**What to build:** All seven breakdown pages share one control header, and none of them
implements preference persistence or date arithmetic any more.

Move the tools, errors, schedules, memory, latency and voice pages onto the wrapper built
in ticket 06. Each keeps everything below the header: its own headline figures, its own
chart type, its own event table and its own sorting.

The six differ in ways the wrapper already has answers for. Errors and schedules have no
metric pill at all. Tools and memory disable metric values that do not currently apply.
Voice has an extra aggregate pill, and it is the real test of this ticket — it is the
only page using the extra-controls fragment, so if that escape hatch is wrong, voice is
where it shows. Latency draws a trend panel alongside its chart, but its second request
lives in the family's refresh, so at the page level it is straightforward. Memory reads
the shared summary store as well as its own.

The overview page is not part of this. It has a time pill but no breakdown, and it stays
as it is.

**Blocked by:** 06 — the session and the wrapper are built and proven there.

**Status:** done

- [x] All seven breakdown pages render their header through the wrapper.
- [~] No breakdown page reads or writes a preference, and none derives a date range. Partial: no page
  derives a date range, and no page reads or writes a *pill* preference. The memory page still reads
  and writes `memory.activeTab` itself, because the tab is the page's own state rather than one of
  the family's choices, so the control header has no business remembering it.
- [x] Errors and schedules render no metric pill.
- [x] Tools and memory still disable the metric values they disable today.
- [x] Voice renders its aggregate pill through the extra-controls fragment, and choosing an aggregation still refreshes with that choice.
- [x] Latency still draws its trend panel, and the trend and the breakdown still move together.
- [x] Memory still shows the figures it takes from the shared summary store.
- [x] Every page keeps its headline figures, chart type, event table and sorting unchanged.
- [x] Every preference a user has saved still loads and still selects the same pills, on all seven pages.
- [x] The overview page is untouched.
- [x] The session tests are parameterised across all seven families.
- [x] The dashboard's Playwright suite passes unchanged.
