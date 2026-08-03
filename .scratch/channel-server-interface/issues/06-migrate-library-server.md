# 06 — Migrate the library server

**What to build:** The library server runs through the shared registration with the gate-on-live policy. Like the scheduling server it is dual-role — a channel for download-completion alerts and a tool and filesystem server — and its channel-protocol tools stay hidden from the language model.

The same gate-on-live reasoning applies: the download watcher drops its routing entry only when delivery is confirmed, so a disconnected-but-still-buffering subscriber must not read as delivered.

One detail specific to this server: only the agent's channel connection ever long-polls it. Its per-conversation tool sessions never poll, so no client-name filter is needed — the distinction is structural. The migration must not accidentally reintroduce one.

This server's notification-emitter interface is removed on the same grounds as the scheduling server's: one production implementation, no test double, consumer in the same project, no domain consumer.

**Blocked by:** 02

**Status:** done

- [x] The library server is registered through the shared call with the gate-on-live policy.
- [x] With no live subscriber, emitting buffers nothing and reports false.
- [x] The download watcher still drops its routing entry only on a confirmed delivery.
- [x] Tool sessions still never register as channel subscribers, with no client-name filter added.
- [x] The server's notification-emitter interface is deleted and its consumer takes the concrete emitter.
- [x] The library project no longer contains its own transport tool, error filter or emitter.
- [x] The server's channel-protocol tools remain hidden from the language model.
- [x] The server's filesystem and search tools are unaffected.
- [x] The existing channel conformance theory still passes.
