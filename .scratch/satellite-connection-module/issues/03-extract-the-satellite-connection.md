# 03 — Extract the satellite connection and prove the seam

**What to build:** one run of the hub's link to a satellite becomes a **satellite
connection** — a module that owns registering, launching the playback and conversation
tasks, routing the five Wyoming frame types, and unwinding in order. The voice host is
reduced to what is genuinely its own: discovering which satellites have an address,
dialling them, parsing addresses, and reconnecting forever.

For a household member nothing changes. Wake, speak, get a reply, follow up, drop the
link and reconnect all behave exactly as before.

What changes for a developer is that the behaviour becomes reachable without a socket.
The host exposes an internal operation that takes a satellite id, its configuration and a
writer delegate and returns a fully wired connection — building the **satellite session**,
the capture session, the conversation coordinator and the connection itself, with the real
transcription, verification and telemetry helpers bound. A test constructs the host with
fakes, asks for a connection, and pushes Wyoming events into it through a channel while
recording what the writer receives. Because the host does the wiring, those tests exercise
the real publishing code rather than stand-ins for it.

The teardown rule stops being a comment. Unwinding splits into a synchronous phase that
releases the arbiter registration and an asynchronous phase that drains everything else,
so "the arbiter goes first, before anything unbounded" is carried by which method the call
sits in. A satellite whose link just died must stop being an arbitration candidate before
any await, or it can still win a wake against a live satellite and silently suppress a
real command.

This ticket ports the four wake and routing tests, so the module is covered by tests the
moment it lands. Ticket 04 ports the rest.

**Blocked by:** 01, 02.

**Status:** resolved

- [x] A satellite connection module owns registration with the session registry and the
      wake arbiter, launching the playback and conversation tasks, routing all five frame
      types, and the ordered unwind.
- [x] It takes the process-wide collaborators as constructor arguments and its
      per-connection collaborators as required init properties, matching the idiom the
      conversation coordinator already uses. *Adapted:* the constructor takes the session
      registry, the wake arbiter, the active-alert registry, the time provider and a logger.
      The spec's list named the voice settings instead of the alert registry, but the
      connection reads no setting and does acknowledge alerts on both wake frames.
- [x] The writer arrives at construction, not as a run-operation argument — the
      coordinator's end-of-turn write and the arbiter's re-arm handle both need it before
      the read loop starts.
- [x] The inbound event stream is passed to the run operation as an async sequence. No new
      interface is introduced over the Wyoming client; the host keeps ownership of the
      client and disposes it when the run returns.
- [x] The run operation still throws when the link drops, so the host's existing reconnect
      loop catches and retries unchanged.
- [x] The host exposes an internal assembly operation returning a fully wired connection.
      The per-connection method becomes dial, create, run.
- [x] The unwind splits into a synchronous phase releasing the arbiter registration and an
      asynchronous phase draining the rest — dispose the coordinator, complete the playback
      channel, await both background tasks swallowing their faults, clear the session's
      control writer, unregister the session — in that order.
- [x] The coordinator and the two background-task fields stay nullable and null-checked in
      the drain, so a connection whose setup threw partway still unwinds cleanly without
      leaving a registered session holding a writer over a disposed client. *Adapted:* only
      the two background-task fields are nullable. The spec's assembly decision has the host
      build the coordinator and hand it over, which makes it a required init property that
      exists before the run starts — there is no window in which it can be null, and
      disposing a coordinator that never ran is a no-op.
- [x] The audio-start payload builder, the playback frame writer, the audio format reader
      and the chunk conversion move onto the connection with the playback wiring. The
      audio-start builder's existing unit tests move with it under a matching name, no
      assertion changed.
- [x] The voice subsystem architecture rules are updated where they name the audio-start
      builder's old owner.
- [x] A new unit test asserts that with the playback task still draining, the arbiter
      registration is already released.
- [x] Four test methods move from the socket-backed integration suite to a new unit suite
      for the connection, assertions verbatim: the command running straight on from the
      wake word using the room level the satellite measured; wake metadata attributed to
      the turn that opened it, in both frame orders; a wake followed by silence re-arming
      without waiting out the maximum utterance; and an active alert acknowledged by a wake
      that produced no utterance.
- [x] The remaining eleven integration tests still pass over real sockets, untouched.
- [x] The multi-satellite arbitration integration tests compile and pass unchanged — this
      is the cheapest check that the extraction did not change what starting the hosted
      service does.
