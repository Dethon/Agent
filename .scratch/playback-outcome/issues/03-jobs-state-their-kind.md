# 03 — Jobs state their kind and the queue owns the limits

**What to build:** a timer and an alarm still play on the satellite's non-attenuated
alert route while an answer, an announcement and a confirmation prompt stay at the
calibrated conversational level, and an answer's sentences still get their own queue
allowance rather than competing with announcements. What changes is that a producer now
says what kind of thing it is queueing, once, and the **playback queue** derives the rest.

Today the same fact is spelled three ways: a label prefix that tests match on as strings,
a depth limit each producer reads from settings and passes in, and an alert flag that any
producer could set but exactly one does. A kind on the job replaces all three, so the rule
"only timers and alarms are alert-routed" becomes a property of the type rather than a
convention held up by a comment.

The reply path currently checks the queue's depth against a limit it reads itself, before
consuming text it would otherwise lose to a refusal. Since the limit moves into the queue,
that check becomes a question the queue answers.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] A playback kind — reply, preamble, announcement, alarm, earcon, confirmation prompt
      — is a required part of every job, and the label stays as free text for logs.
- [ ] The queue takes both depth limits at construction and picks between them by kind: an
      answer's segments get the reply allowance, everything else the announce allowance.
- [ ] No producer passes a depth.
- [ ] The alert route is chosen by the alarm kind. The alert flag is gone, and priority is
      still not the marker, because confirmation prompts share the high priority.
- [ ] The queue answers whether it can accept a given kind, replacing the public depth
      reader, and the reply path uses it before taking text out of its buffer.
- [ ] The queue's own tests assert the alert route and both depth limits by kind rather
      than by label string.
- [ ] The existing audio-start tests still prove that a marked stream reaches the wire as
      an alert and an unmarked one does not.
- [ ] All producer tests and the voice integration suite pass.
