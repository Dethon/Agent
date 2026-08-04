# 03 — The conversation group gets a module

**What to build:** Everything a conversation group owns for the length of its life lives
in one place, so the order it is established in is the order of statements inside a module
rather than an argument spread across a file.

A conversation group module is constructed per conversation and agent. It owns the
pending-turn queue, the command dispatch loop, the group anchors, the agent, the restored
thread, the warmup task and the running of turns, and it exposes running the group's
messages as reply updates, plus disposal. The rules that are comments today — a command is
dispatched immediately and never queues, turns run one at a time, the warmup is awaited
before the first stream — become internal to it.

The chat monitor keeps merging the channel streams, grouping the messages, constructing a
group per key, delivering each update to its targets and reporting first-reply latency.

The module is internal and constructed only by the chat monitor. It does not become a test
seam; everything about it is asserted through the monitor.

Construction order does not change in this ticket. The group still reads its first message,
resolves its anchors, builds its agent, restores its thread and starts its warmup before
anything is parsed, and the message index is still what target resolution and the announce
read. Behaviour is identical. This is the last of three preparatory steps.

**Blocked by:** 02.

**Status:** done

- [x] A conversation group module owns the queue, the command dispatch, the anchors, the agent, the thread, the warmup and the turn loop.
- [x] It is internal, constructed only by the chat monitor, and disposable.
- [x] The chat monitor is left with merging, grouping, delivering and reporting first-reply latency.
- [x] The monitor's private turn-scope record, its group-anchor resolution, its queuing loop, its turn runner and its per-turn target resolution no longer exist outside the module.
- [x] The thread context is still created and its completion callback registered before any message is parsed.
- [x] No new test seam is introduced; the module is not driven directly by any test.
- [x] The existing monitor test suite passes unchanged.
