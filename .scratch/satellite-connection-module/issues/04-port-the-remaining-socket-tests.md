# 04 — Port the remaining socket-backed tests

**What to build:** the ten remaining behaviours still proved over a real TCP socket move to
the **satellite connection** unit suite, driven by pushing Wyoming events through a channel
and asserting on a recorded writer. Their assertions do not change; what disappears is
roughly a hundred lines of listener and fake-satellite plumbing per test.

One test method stays behind in the integration suite — the full turn from dial through
framing to transcript — so that dialling, the Wyoming framing and the hosted service keep
being proved together.

Four of the ported tests assert on real metric publishes. They must keep doing so: because
the host assembles the connection, the real transcription, verification and telemetry code
runs in these tests. Replacing those helpers with test delegates would hollow out the
coverage and would leave the voice half of the metrics-publishing work still blocked.

**Blocked by:** 03.

**Status:** resolved

- [x] Ten test methods move to the connection's unit suite with assertions verbatim: the
      dispatch stamp taken before the dispatch; a conclusive speaker emitted as the sender;
      an unknown speaker rejected before recognition with its metric; the early mark keeping
      the microphone open when no speech has landed yet; the early mark rejecting a
      continuous unknown voice; both telemetry-down paths still rejecting and re-arming; a
      follow-up turn dispatched without a second wake; a follow-up silence re-arming with a
      closing transcript; and an active alert acknowledged by a dispatched utterance.
- [x] The four tests that assert on metric publishes exercise the host's real publishing
      code, reached through the host's assembly operation, not through test-supplied
      delegates.
- [x] The shared helpers those tests rely on — the PCM builders, the speaker verifier
      doubles that drive accept, reject and skip by peak sample, and the one-reply-segment
      helper — move with them.
- [x] Exactly one test method remains in the socket-backed integration suite, covering dial,
      framing, a full turn and the hosted service together.
- [x] The multi-satellite arbitration integration tests remain untouched and green.
- [x] No behaviour assertion is weakened, dropped or merged in the move. Every ported test
      is traceable to the original it replaces.
