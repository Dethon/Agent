# 05 — The earcon and the confirmation prompt await their outcome

**What to build:** the earcon that tells a household member the microphone is open still
plays fully before the microphone opens, and a confirmation prompt still finishes speaking
before the hub starts listening for "sí" or "no". A prompt whose satellite disappeared
before the question could be queued still abandons the approval cleanly rather than opening
a microphone on a dead connection. What changes is how both of them wait: one await on the
job's outcome, instead of a completion source each settles by hand from three of the five
callbacks.

These two producers wrote the same idiom independently, and both are correct by inspection
only — nothing stops the next one from wiring two of the three callbacks and hanging until
the reply timeout. After this ticket there is nothing to wire.

Both keep their cancellation tokens. Neither needs one any more to avoid hanging, but each
has its own reason to stop waiting: the earcon's connection can tear down, and an approval
request can be cancelled by the agent. That is now the only thing those tokens mean.

**Blocked by:** 04. Runs in parallel with 06 and 07.

**Status:** resolved

- [x] The earcon queues its job and awaits the outcome, with its hand-rolled completion
      source and its three callback wirings gone.
- [x] The confirmation prompt does the same, for both the question and the re-prompt after
      a misheard answer.
- [x] A refused prompt abandons the approval, which is what the current early return on a
      rejected enqueue does.
- [x] Both keep their cancellation tokens, and a comment records that the token now means
      "this caller has its own reason to stop waiting", not "otherwise this hangs".
- [x] The three spin-waits in the confirmation-prompt tests become awaits on an outcome.
- [x] At the satellite connection seam, a test proves the earcon returns when its outcome
      arrives, and a test proves a connection dropped mid-playback discards what was
      queued. Both are new coverage: the earcon is unreachable in a unit test today.
- [x] The follow-up conversation tests and the voice integration suite pass.
