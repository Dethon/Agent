# 03 — Recall memory under the delivery identity

**What to build:** A memory extracted during a scheduled run records, as its source, the conversation the user can actually open — not the synthetic scheduling id. The same for the recall event and the recall latency the run publishes.

The memory recall hook is handed the message's own conversation id today, which makes it a fourth name for the concept ticket 01 collapsed. It is handed the turn's delivery identity instead. That single change moves three things: the recall event's conversation id, the recall latency event's conversation id, and the source provenance carried on the extraction request and stamped on any memory the turn produces.

This is the one part of the work that changes durable data. Existing memory records are not migrated: memories written from a scheduled turn before this lands keep pointing at a synthetic scheduling id, and memories written after point at a real conversation. That is accepted, for the same reason the metric split is accepted — nothing rewrites history, and both forms are readable.

Nothing inside the recall hook changes. Its own tests already cover what it does with the conversation id it receives; this ticket is about which id it receives.

**Blocked by:** 01 — Resolve the delivery identity once and build the turn from it.

**Status:** done

- [x] A schedule fire that mints a WebChat conversation passes the minted id to the memory recall hook, asserted in the delivery-identity unit file from ticket 01
- [x] A plain WebChat message still passes its own conversation id
- [x] The recall hook's own tests and behaviour are unchanged
- [x] `dotnet test Tests/Unit` passes
