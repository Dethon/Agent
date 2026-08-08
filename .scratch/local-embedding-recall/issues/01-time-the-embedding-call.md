# 01 — Time the embedding call separately from the recall stage

**What to build:** The recall stage reports one duration that hides its own cause. Measured
in production it is 575 ms, while the embedding call inside it measures 361 ms and storage
accounts for roughly 15 ms. The remaining time is attributed to connection setup by
inference, not by measurement.

Split the measurement so the embedding call is timed on its own, alongside the existing
stage measurement. Someone reading metrics should be able to say how much of a recall was
the embedding round trip and how much was everything else, without inferring it.

This lands before anything else changes, so the work that follows has a real baseline to be
compared against rather than a claim.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] The embedding call publishes its own duration, attributable to the turn it belongs to
- [x] The existing recall stage measurement keeps its current meaning and is not redefined
- [x] Both are broken down the same way the existing latency stages are, so they can be
      compared per conversation and per agent
- [x] The measurement is taken whether the call succeeds or fails
- [x] Nothing about what recall returns changes
