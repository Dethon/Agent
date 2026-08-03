# 05 — Migrate the scheduling server

**What to build:** The scheduling server runs through the shared registration with the gate-on-live policy. It is a dual-role server — both a channel and a tool server — and its channel-protocol tools stay hidden from the language model.

Gate-on-live is load-bearing here and must be preserved precisely. The dispatcher deletes or advances a schedule only when delivery is confirmed. If a failed delivery still buffered the item, the schedule would be kept **and** a duplicate would be sitting in the inbox, so the task would fire twice. Nothing buffered on a false return is the whole point of the policy.

This server's notification-emitter interface is removed as part of the migration. It has one production implementation, no test double, and its consumer sits in the same project, so the consumer takes the concrete emitter. This does not conflict with the recorded decision to keep single-adapter interfaces in the domain contracts folder — that interface lives in this server's project and has no domain consumer.

**Blocked by:** 02

**Status:** done

- [x] The scheduling server is registered through the shared call with the gate-on-live policy.
- [x] With no live subscriber, emitting buffers nothing and reports false.
- [x] The dispatcher still deletes or advances a schedule only on a confirmed delivery.
- [x] A failed delivery leaves no buffered duplicate behind, so the schedule fires once when it next succeeds.
- [x] The server's notification-emitter interface is deleted and its consumer takes the concrete emitter.
- [x] The scheduling project no longer contains its own transport tool, error filter or emitter.
- [x] The server's channel-protocol tools remain hidden from the language model.
- [x] The existing channel conformance theory still passes.
