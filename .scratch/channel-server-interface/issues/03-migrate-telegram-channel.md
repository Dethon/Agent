# 03 — Migrate the Telegram channel

**What to build:** The Telegram channel runs through the shared registration with the buffer-always policy. It is the only server using that policy, so this ticket is what proves targeted buffering works through the shared module.

Buffer-always exists because Telegram has no transport-level way to tell a sender "try again later" — a message arriving during a server cold start, or just after an idle eviction, must be buffered rather than fanned out to nobody. That behaviour must be preserved exactly.

The subscriber id this channel targets must match the id the agent's channel connection derives for itself. If it does not, items are buffered into a queue nobody ever drains, and nothing reports an error — the failure is completely silent. Pin the match with a test rather than trusting the constant.

The Telegram bot service currently only warns when nobody is listening; it does not gate on it. That stays true — the message is emitted either way. The warning now comes from the emitter's return value rather than a separate property read.

**Blocked by:** 02

**Status:** ready-for-agent

- [ ] The Telegram channel is registered through the shared call with the buffer-always policy and its subscriber id.
- [ ] A test asserts the emitter's target subscriber id equals the id the agent's channel connection derives, so a mismatch cannot pass silently.
- [ ] A message arriving before any poll is buffered and delivered to the first poll that arrives.
- [ ] The bot service still emits the message when nobody is listening, and still warns.
- [ ] The Telegram project no longer contains its own transport tool, error filter or emitter.
- [ ] The Telegram emitter test is narrowed to its own payload shape; its liveness assertions are removed.
- [ ] The existing channel conformance theory still passes.
