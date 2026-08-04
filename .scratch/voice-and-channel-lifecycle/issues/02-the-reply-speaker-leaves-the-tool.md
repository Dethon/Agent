# 02 — The reply speaker leaves the tool

**What to build:** The reply policy inside `McpChannelVoice/McpTools/SendReplyTool.cs`
becomes a **reply speaker** — a DI-registered module holding its collaborators as fields
— and the MCP tool becomes the thin entry point it is supposed to be.

Today the file is 470 lines. `McpRun` resolves seven services at `:36-42` and two more
conditionally at `:51` and `:59`, then hands them down through seven private statics
whose parameter lists run to nine and ten entries (`:66`, `:134`, `:170`, `:195`,
`:218`, `:260`, `:428`). Because the whole chain is static, a test cannot call any of it
without standing up a service provider. Four test files reference `SendReplyTool`:
`SendReplyToolTests.cs` (two provider builds, `:80-89` and `:462-470`),
`SendReplyToolScheduledDeliveryTests.cs`, `TurnLatencyDecompositionTests.cs` and
`SatelliteConnectionTests.cs`. Check the last one before assuming it needs changing —
it may reach the tool through a connection rather than through a provider it built.

After this ticket the reply speaker holds the reply accumulator, the TTS client, the
voice settings, the metrics publisher, the `TimeProvider` and the logger as fields, and
offers two entry points: the live utterance reply and the scheduled delivery. The private
statics become methods and their parameter lists become `(session, params)`.

**Both branches go in one module.** They differ — one speaks into a live session, the
other settles a durable delivery record through `AnnouncementService` — but they share
the accumulator, and splitting by branch would put one collaborator in two places.

**The static-plus-`IServiceProvider` shape stays.** It is a repo-wide MCP tool convention
and it is not the problem. `McpRun` resolves the speaker, resolves the session through
the registry and the conversation manager, and calls one of the two entry points. The
choice of branch — live session or scheduled target — stays where it is.

Behaviour must not change, including the two things the current code comments defend:
`TimeProvider` is resolved only on the live path (`:47-51`), and the args dictionary on
the send path is built by hand to avoid reflection on a per-chunk hot path.

Start red: pick one behaviour currently asserted through a `ServiceCollection` — the
first-segment minimum character rule at `:245` is a good one — write it against the
speaker constructed directly, watch it fail, then build.

**Blocked by:** `01`. Six of this file's lines are identity-triple sites that `01`
rewrites; going first means writing them twice.

**Status:** ready-for-agent

- [ ] A reply speaker module holds the accumulator, TTS, settings, metrics, `TimeProvider` and logger as fields.
- [ ] It has two entry points, live utterance reply and scheduled delivery.
- [ ] `SendReplyTool.McpRun` keeps its static-plus-`IServiceProvider` signature and contains no reply policy.
- [ ] No private static in `SendReplyTool` takes more than four parameters, or there are none left.
- [ ] The test files construct the speaker directly; no test builds a `ServiceCollection` to reach reply policy.
- [ ] The `TimeProvider`-on-the-live-path-only resolution and the hand-built args dictionary both survive, with their comments.
- [ ] Voice E2E and the existing reply tests pass unchanged.
