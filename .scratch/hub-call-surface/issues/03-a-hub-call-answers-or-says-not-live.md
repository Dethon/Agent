# 03 — A hub call answers or says it was not live

**What to build:** the ability, for the first time, to ask the server something and be
told that the question could not be asked. Today a **hub call** made while the client
is between connections comes back with the same empty value the server itself returns
when there is genuinely nothing there, and no signature distinguishes the two.

The **live connection** gains three verbs — a typed invoke, a void invoke and a
stream — each answering with either the server's answer or **not live**. It is the
only thing that can decide, because it owns the current **hub connection** and knows
whether it is live. The same three verbs go onto the hub connection abstraction so
the module can be driven against its fake.

The shape, which the rest of this feature is written against:

```csharp
readonly record struct HubResult<T>(bool IsLive, T? Value);
```

Two cases and no more. Not live never means the server said no: a server that answers
`false` is live and has answered. So the boolean-valued calls end up carrying three
outcomes, and later tickets must keep the middle one distinct from the first.

The rule covers more than today's guards did. They check only for a missing
connection, which happens between a teardown and the next successful start. A
connection that is connecting or reconnecting is present and cannot carry a call, and
those states answer not live too — that is the hole worth closing first.

Nothing else moves in this ticket. The raw connection accessor stays, every caller
keeps reaching through it, and no service or effect changes.

The probe keeps its current shape and does not answer with a result. It is what asks
whether the connection is live, so it cannot be an answer that depends on being live.

**Blocked by:** None in this set. Requires the chat live connection work
(`.scratch/chat-live-connection/`) to have landed, since it renames this module and
adds the receive verb to the same seam.

**Status:** ready-for-agent

- [ ] The result type exists and expresses exactly two cases.
- [ ] The live connection exposes a typed invoke, a void invoke and a stream verb,
      each answering with a result.
- [ ] The hub connection abstraction exposes the same three verbs, and its fake can
      be scripted to answer them.
- [ ] Each verb returns the server's answer when the connection is live.
- [ ] Each verb answers not live when there is no connection, when one is connecting,
      and when one is reconnecting.
- [ ] The probe is unchanged and does not answer with a result.
- [ ] Nothing outside the connection module changes, and the full client suite passes.
