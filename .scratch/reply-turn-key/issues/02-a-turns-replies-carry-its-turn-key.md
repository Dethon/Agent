# 02 — A turn's replies carry its turn key

**What to build:** every reply the agent sends says which turn it answers.

A message arriving at the agent can carry a **turn key**. The conversation group mints one as
it builds the turn whenever the inbound message has none, so from that point on every turn has
a key regardless of which channel the message came from. The turn carries it, and every reply
produced for that turn echoes it back to the channel — including the synthesized
stream-complete event, which is exactly where today's message id goes null.

Each reply also says whether the turn it answers was agent-initiated, derived from the message
origin the conversation group already tests when it announces a turn start. Without it a
scheduled delivery and an abandoned answer are indistinguishable at the receiving end: both
carry a key that does not match the live turn's, and the two have to be treated oppositely.

Every channel's `send_reply` accepts both new fields and hands them back unchanged — the four
channel tools plus the shared no-outbound-surface stub. No channel reads them yet.

**Blocked by:** 01 — send_reply takes the params record.

**Status:** ready-for-agent

- [ ] The inbound channel message carries an optional turn key.
- [ ] The conversation group mints a turn key when the inbound message carries none, and keeps
      the one it was given otherwise.
- [ ] Every reply produced for a turn carries that turn's key, including the stream-complete
      event and error chunks.
- [ ] Every reply carries whether its turn was agent-initiated, derived from the message origin.
- [ ] Two turns in one conversation produce two different keys; every update of one turn carries
      the same key.
- [ ] All four channel `send_reply` tools and the shared no-outbound-surface stub accept both
      new parameters and round-trip them unchanged.
- [ ] Proved at two existing seams: the monitor's delivery identity tests for minting and echo,
      and the per-channel contract pin for the round trip. No new seams.
