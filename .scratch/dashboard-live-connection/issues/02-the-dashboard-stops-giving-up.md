# 02 — The dashboard stops giving up

**What to build:** Someone leaves the dashboard open, the agent gets redeployed, and two
minutes later the dashboard is receiving events again without anyone touching it.

Today it is not. The dashboard asks for automatic reconnection without saying how, so it
gets the framework's default: four attempts spread over about forty-two seconds, and
then it stops permanently. Any outage longer than that kills the live feed for good, and
a container restart is longer than that. The page keeps showing a green Live dot over
numbers that will never change again.

Replace that with an explicit policy: zero, two, ten and thirty seconds, then thirty
seconds forever. It never returns the value that means stop. Thirty seconds is the
steady interval because it is the last of the framework's own defaults, so this is
exactly "keep going" rather than a new schedule with new numbers to justify.

The policy is a pure function of the retry context and lives on its own, so it can be
read and tested without standing up a connection.

This does not cover a dashboard that was never connected in the first place. Automatic
reconnection has never applied to the first attempt — that is documented framework
behaviour, not a bug — and ticket 03 is what covers it. A change that only replaces the
policy leaves a dashboard opened during a restart just as dead as it is today.

**Blocked by:** 01 — the concrete client this configures is rewritten there.

**Status:** ready-for-agent

- [x] The metrics hub connection is built with an explicit retry policy rather than the parameterless automatic-reconnect call.
- [x] The policy's first four delays are zero, two, ten and thirty seconds.
- [x] Every delay after those is thirty seconds.
- [x] The policy never returns the value that means stop, however many attempts have been made and however long reconnection has been going.
- [x] The policy is testable as a pure function, with no connection involved.
- [x] An outage longer than the previous forty-two second window recovers on its own.
