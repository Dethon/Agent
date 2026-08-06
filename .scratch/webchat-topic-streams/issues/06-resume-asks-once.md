# 06 — Resume asks once

**What to build:** resuming becomes a state of the same per-topic record, so the client asks one
question instead of three.

Today the resume path checks whether a topic is already resuming, then whether it is already
streaming, then asks the streaming service the same thing again with a lock held — three answers
against three staleness windows, any of which can be true while another is false. After this
ticket, asking the module to resume a topic either grants a lease or refuses, and that single
answer covers all three cases. The separate set tracking which topics are resuming leaves the
store.

For a user: a reply that started while they were disconnected is picked up exactly once, and a
network that drops and recovers twice in quick succession does not multiply the reply.

This is the last ticket that can change what the store publishes for rendering, so it is where
the browser suite runs.

**Blocked by:** 05 (both remove state from the same store slice).

**Status:** ready-for-agent

- [ ] Asking the module to resume a topic grants a lease when the topic has nothing in flight, and
      refuses when it is already resuming or already streaming.
- [ ] The resume service makes one such request instead of its three separate checks, and no
      longer needs the streaming service to answer questions about state.
- [ ] The effect that reacts to a pushed stream start asks the module whether the topic is already
      resuming, not the store.
- [ ] The set of resuming topics is gone from the store's streaming state, and nothing reads it.
- [ ] Two pushed stream starts arriving back to back for one topic resume it once.
- [ ] A resume that finds no stream in progress on the server leaves the topic idle and marks
      nothing; a resume whose hub call could not be made says nothing either way, as today.
- [ ] The resume service's collaborator count drops, and its tests construct it with the smaller
      set.
- [ ] `dotnet test` on `Tests/Unit` is green.
- [ ] The WebChat E2E suite passes: the Cancel button appears while a reply is being written and
      disappears when it ends, the sidebar streaming indicator matches, and a resumed stream still
      raises a pending approval.
- [ ] `dotnet format` has run over the staged files.
