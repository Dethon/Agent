# 02 — The metrics endpoints take a bound date range

**What to build:** Asking any metrics endpoint without a date range answers for today,
and that rule exists in one place instead of twenty-two.

Every route in the metrics group currently declares two nullable date parameters and
then fills each one from the system clock in its own body. The service sitting directly
behind those routes already takes a time provider and uses it; the endpoints in front of
it read the clock directly, so the query side is time-testable and the endpoint side is
not.

Replace the pair of nullable parameters with a single bound date range. It reads the
same two query values and fills either default from the time provider resolved out of
the request services. The query string is unchanged, so no client changes and no route
changes — a hand-typed URL behaves exactly as it does today.

This ticket is independent of the dashboard client work and can run alongside it. Note
that it edits the same endpoint file that ticket 01 renames a type in.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A date range type binds itself from the request, reading the existing from and to query values.
- [ ] A missing value defaults to the current date taken from the time provider, never from the system clock directly.
- [ ] Every route in the metrics group takes the bound range in place of two nullable parameters.
- [ ] The observability service reads the clock in exactly one place for this purpose.
- [ ] Requests are unchanged on the wire: same paths, same query parameter names, same responses for the same inputs.
- [ ] Unit tests drive the binder against a request context carrying query values and a fake time provider, covering both values absent, one present, both present, and unparseable input.
- [ ] Those tests run standalone, with no Redis and no browser.
