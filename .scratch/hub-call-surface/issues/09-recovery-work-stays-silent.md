# 09 — Recovery work stays silent

**What to build:** the calls the client makes for its own reasons — rejoining a space,
identifying the user, resuming a stream, and the three push-subscription calls — answer
or say **not live**, and say nothing to the user when they could not be made. The user
did not ask for any of them, they are retried on **becoming live**, and an error toast
for one would be noise about something the user can do nothing about.

This is the last batch, and it carries one move. Identifying the user is the single
hub call with no service in front of it: it is made straight through the raw
connection from an effect. It moves onto the session service, which already owns the
client's session with the server, so the session recovery introduced by the chat live
connection work takes a typed dependency it can fake instead of reaching for a
transport.

The push service keeps its existing behaviour on unsubscribe, where a failure from the
server is deliberately swallowed: the client-side subscription is already gone by then,
and a failed server-side cleanup is not the user's problem. What changes is that "we
never asked" stops being indistinguishable from "the server refused".

**Blocked by:** 03, 04.

**Status:** done

- [x] The space join, user registration, stream resume and three push calls answer
      with a result.
- [x] Identifying the user is a method on the session service, and session recovery
      depends on that rather than on a transport.
- [x] None of these raises a toast when the call could not be made.
- [x] None of them disturbs a store when the call could not be made.
- [x] The push service's unsubscribe still swallows a server-side failure, and its
      existing suite passes.
- [x] When the transport is live, all six behave exactly as they do today.
