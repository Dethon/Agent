# 05 — The sidebar survives an interruption

**What to build:** a user who picks a different agent a few seconds after their phone
wakes up keeps their conversation list. Today it empties. The client is between
connections, the topic fetch comes back with an empty list, and the effect stores
that as the truth — so an interruption looks exactly like having no conversations.
The transcript goes the same way when a history fetch lands in the same window.

The topic and history calls answer or say **not live**. The effects that consume them
skip their dispatch when the answer could not be made, leaving the store holding what
it already had. Nothing further is needed to recover: the **connection epoch** from
the chat live connection work already reloads topics and history on **becoming live**,
so the list refills by itself a moment later.

This is the headline defect of the whole feature. Write its test first and watch it
fail against the current design before changing anything.

No toast here. The user did not ask for either of these calls — they are reads that
feed a store, and the rule for those is to stay quiet and keep what is on screen.

**Blocked by:** 01, 03, 04.

**Status:** done

- [x] The topic list and history calls answer with a result rather than an empty list.
- [x] Their callers skip the store dispatch when the answer is not live.
- [x] A populated topic list survives an agent switch made while the transport cannot
      carry a call — the failing-first test.
- [x] A populated transcript survives a history fetch that could not be made.
- [x] Neither case raises a toast.
- [x] When the transport is live, both calls behave exactly as they do today,
      including a genuinely empty answer emptying the list.
- [x] The existing agent selection, topic selection and initialization suites pass,
      adjusted only for the changed signatures.
