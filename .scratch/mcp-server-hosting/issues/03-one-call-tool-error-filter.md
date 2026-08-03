# 03 — One call-tool error filter

**What to build:** The rule that a cancelled tool call must propagate as the abort it is,
while any other exception becomes the caller's error result, currently lives inside
`AddChannelServer`. Ticket 05 needs the same rule for tool servers, and the two dual-role
servers will ask for both — so the rule has to survive being requested twice.

Pull it out into one shared registration that installs the filter at most once and hand
`AddChannelServer` that instead of its own copy. A second request is a no-op, so two
filters nested around each other — where the outer would convert the very cancellation
the inner deliberately rethrows — stops being expressible.

Behaviour is unchanged in this ticket. Nothing new asks for the filter yet; the change is
that the rule now has one home and can be asked for safely.

**Blocked by:** 01 — Rename the hosting project to Mcp.Hosting; 02 — A server contract
table covering all thirteen.

**Status:** ready-for-agent

- [ ] The filter is one shared registration; `AddChannelServer` asks for it rather than
      building its own.
- [ ] Asking for it twice on the same server yields one filter. Assert this directly — it
      is the whole reason the ticket exists.
- [ ] The caller still supplies its own error shape, and a caller that supplies none still
      gets the plain default.
- [ ] A cancelled call still does not become an error result, and any other exception still
      does. The existing tests for both already run against a real in-memory server over
      the wire; keep them there rather than reasserting at the registration level.
- [ ] The six existing channel-server rows in the contract table stay green with nothing
      edited.

## Notes

The rule's existing comment explains why cancellation is special — a long poll ends in
cancellation whenever the agent hangs up, and mapping that to an error result hands the
agent's pump something to retry. Carry that reasoning to wherever the shared registration
ends up. It is about to apply to seven more servers that never had it, so it needs to read
as a general rule rather than a channel-specific one.

The first registration wins. Both dual-role servers pass the same error shape today so
nothing changes, but assert it rather than assuming it — ticket 05 is what makes the
ordering observable.
