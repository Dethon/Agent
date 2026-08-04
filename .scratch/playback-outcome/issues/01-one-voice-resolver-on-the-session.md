# 01 — One voice resolver on the satellite session

**What to build:** a satellite configured with its own text-to-speech voice keeps
speaking in that voice, in every situation where it speaks — an answer, a confirmation
prompt, a re-prompt after a misheard answer, and a plain announcement. Nothing a
household member hears changes. What changes is that the rule "use this satellite's
voice, otherwise the global one" is written once on the **satellite session** instead of
being spelled out at four call sites.

This is a prefactor. Three of those four call sites sit in files that later tickets
rewrite, and the fourth is in the announcement service, which ticket 07 touches. Doing
it first means none of those tickets has to carry the duplication across its own change,
and a satellite whose configured voice is currently honoured in three places and could
be missed in a fourth stops being a possibility.

The alarm controller is deliberately excluded: it synthesises each alert once and
replays it to every target, so it has no per-satellite voice to resolve and its comment
says so.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] The satellite session exposes one operation that takes the voice settings and
      answers with this satellite's configured voice, falling back to the global voice.
- [x] The reply path, both confirmation-prompt paths and the announcement service use it,
      replacing their own fallback expressions.
- [x] An explicitly requested voice still wins where a caller accepts one: the
      announcement service keeps preferring the voice on the request.
- [x] The alarm controller is left alone.
- [x] Unit tests cover a satellite with a configured voice, a satellite without one, and
      a satellite whose configured section exists but names no voice.
- [x] The existing reply, confirmation-prompt and announcement tests pass unchanged.
