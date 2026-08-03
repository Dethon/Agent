# 08 — Delete the terminal callbacks

**What to build:** nothing a household member observes changes. This is the contract half of
the expand–contract: with every producer now settling from an outcome, the callbacks they used
to settle from are deleted, and the **playback job** is left carrying one non-terminal
observation — the first-audio timing.

Until this lands, both mechanisms exist side by side and a new producer could still pick the
wrong one. After it, the mutual-exclusion rule that used to be the whole contract is
unrepresentable: there is nothing to be mutually exclusive.

The loop loses four of its swallow-everything guards with the callbacks they protected. It
keeps the guards around the first-audio callback, which is still invoked between two audio
writes, and around its owner's hooks — the frame writer, the audio envelope, the per-job error
metric — which stay awaited because they are the connection's work and are ordered with respect
to the audio they frame.

**Blocked by:** 05, 06, 07.

**Status:** ready-for-agent

- [ ] The started, preempted, drained and failed callbacks are gone from the job.
- [ ] The job carries a label, a kind, a priority, its audio, the first-audio callback and the
      enqueue stamp, and nothing else.
- [ ] The first-audio callback no longer receives a label, because no producer ever read it.
- [ ] The loop keeps its guard around the first-audio callback and around the owner's hooks,
      and has no other swallow-everything guard.
- [ ] The queue's tests no longer reference any terminal callback, and the exactly-one-outcome
      test is the only place the guarantee is proved.
- [ ] The voice subsystem rules name the playback queue alongside the gate factory, the turn
      module and the capture module, and state its promise in one sentence.
- [ ] The whole voice suite passes, unit and integration.
