# 03 — One rejection policy for both patch fields

**What to build:** A user whose override was refused leaves a trace. Today the two halves of the config patch fail differently and both fail silently: a model outside the whitelist falls back with no log, and a reasoning effort that cannot be parsed is swallowed by a caught exception with no log at all. An operator asked why a user's choice did not take has nothing to look at.

Both fields get one rule — fall back to the configured value and warn. Falling back is deliberate: a bad override must never cost the user a turn, on either field. What changes is that the fallback becomes visible.

The warning names the field, the rejected value and the fallback:

```csharp
logger.LogWarning("Rejected config patch {Field}={Value}; using {Fallback}", field, value, fallback);
```

One message shape for both fields, so a reader does not have to check which field they are looking at to know what happened. This also surfaces the case where a client's whitelist has drifted from the agent's: the rejected value in the log is the evidence.

The reasoning effort is the half that never had rejection tests. It gets them here.

**Blocked by:** 02 — both fields must resolve in one place before they can share a rejection path.

**Status:** done

- [x] A refused model override logs a warning naming the field, the rejected value and the fallback, and the turn still runs on the configured model.
- [x] An unparseable reasoning effort does the same, and the turn still runs on the configured effort.
- [x] The caught-and-discarded exception path for the effort no longer swallows the failure silently.
- [x] Both warnings are asserted at the agent seam, alongside the resolution cases from 02.
- [x] A valid override on either field logs no warning.
