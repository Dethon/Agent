# Ziggurat

An AI agent reachable over several transports, with its own tools, memory and
observability. This file is the glossary: what each term means here, and which
near-synonyms not to use for it. It holds no implementation detail.

## Observability

**Metrics publisher**:
The fire-and-forget thing a caller holds to record a metric. Publishing through it
cannot fail, cannot block and cannot be observed.
_Avoid_: metrics client, metrics writer, telemetry publisher

**Metric sink**:
The transport a metrics publisher drains into. Sending through a sink is a real
network operation that may fail.
_Avoid_: metrics backend, metrics transport, metrics exporter

## Chat client connection

**Live connection**:
The client's single ongoing link to the chat hub. It outlives any one transport
instance: it survives being torn down and built again, and while it is live the
client both receives server pushes and can call the server.
_Avoid_: connection service, hub client, socket

**Hub connection**:
One transport instance underneath a live connection. It is disposable and gets
replaced whole; nothing outside the live connection holds one across a replacement.
_Avoid_: connection, channel, socket

**Rebuild**:
Throwing away the hub connection and building a new one. Everything bound to the
old instance is gone afterwards.
_Avoid_: reconnect, restart, refresh

**Reconnect**:
The transport restoring itself without being replaced. The same hub connection
comes back, so anything bound to it is still bound.
_Avoid_: rebuild, retry

**Becoming live**:
The moment the client can talk to the server again, whether it got there by a
rebuild or a reconnect. Callers that must recover after an interruption care about
this, never about which of the two happened.
_Avoid_: connected event, online

**Connection epoch**:
How many times the client has become live. Two epochs being different is the only
reliable way to tell that an interruption happened, because a fast rebuild can
start and finish without anyone observing a disconnected state in between.
_Avoid_: generation, connection id, sequence number

**Session recovery**:
The work that has to happen every time the client becomes live again for the
server to treat it as the same user in the same space: identifying the user,
rejoining the space, re-sending the push subscription. It never runs on the first
connection, where ordinary start-up does that work with the extra steps the first
connection needs.
_Avoid_: reconnection handler, resubscribe, rehydrate

**Hub call**:
Something the client asks the server to do over the live connection and waits on:
fetching topics, sending a message, answering an approval. It goes in the opposite
direction to a server push, and unlike a push it can only happen while the
connection is live.
_Avoid_: invoke, request, RPC, server call

**Not live**:
The answer a hub call gives when it could not be made, because the client was
between connections or still getting one up. It is one of the two things any hub
call can come back with, the other being the server's own answer, and it never
means the server said no.
_Avoid_: disconnected, offline, failed, null

## Voice satellite

**Satellite connection**:
One run of the hub's link to a satellite, from the moment it dials to the moment
it has finished unwinding. It is the thing that runs: nothing outside it holds a
reference to it, and a drop ends it for good — the next attempt is a new one.
_Avoid_: satellite session, link, socket

**Satellite session**:
The satellite as something the rest of the hub can address. Callers that have
nothing to do with the wire — a reply being spoken, an announcement, an alarm —
find it by satellite id and use it to queue audio, send a control event or read
the current turn. It lives exactly as long as one satellite connection, but it is
reached from the outside rather than run.
_Avoid_: satellite connection, satellite state

**Wake announcement**:
What the satellite tells the hub when its own wake detection fires: how loud the
wake word was, how confident it was, what triggered it, and how loud the room was
just before. Every part of it is optional, because it comes from a peer with no
schema and older firmware sends none of it.
_Avoid_: wake signal, wake event, wake metadata

**Wake turn**:
The turn a wake announcement opens. It is the only turn whose loudness is worth
recording, because it is the only one the user announced by speaking the wake word.
_Avoid_: first turn, initial capture

**Follow-up turn**:
A turn the hub opens by itself after a reply, with the microphone live and no wake
word. It has no wake announcement of its own and never will.
_Avoid_: continuation, second turn

## Satellite playback

**Playback queue**:
The one place audio waits its turn to be heard on a satellite. Only one thing is
audible at a time, so everything anyone wants spoken there — a reply, an
announcement, an alarm, a prompt, an earcon — goes through it and is heard in the
order it accepted them.
_Avoid_: playback loop, audio channel, speaker

**Playback job**:
One stretch of audio handed to the queue to be heard as a unit, along with what kind
of thing it is. The kind is what the queue reads to decide how much of that kind it
will hold at once and how it is treated on the way out.
_Avoid_: playback item, utterance, segment

**Playback outcome**:
The single fact about a job that ends it. Every job accepted or turned away gets
exactly one, and never more: it was heard to the end, it was cut short, it broke, it
was turned away, or the connection died before it could be heard. Whoever queued the
job learns it from that one fact and nothing else.
_Avoid_: playback result, callback, completion

**Refusal**:
The queue declining a job outright, so it never waits and is never heard. A refusal
is an outcome like any other, not a failure — the caller is told why, and the three
reasons are that the satellite is gone, that the queue already holds as much of that
kind as it will, and that a low-priority job arrived while anything was queued.
_Avoid_: rejection, drop, error

**Preemption**:
A high-priority job cutting the line. Everything already queued when it arrived is
cut short as well, not just whatever was audible at that moment, so an alarm is heard
next rather than after the rest of an answer.
_Avoid_: cancellation, interruption, barge-in

**Drain**:
A job being heard all the way to its end. It means the satellite finished playing,
not that the hub finished sending — the hub is always done sending first, because the
satellite plays at real time.
_Avoid_: completion, finish, flush
