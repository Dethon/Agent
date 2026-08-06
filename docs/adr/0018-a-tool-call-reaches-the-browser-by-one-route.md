# 0018 — A tool call reaches the browser by one route

Status: accepted
Date: 2026-08-06

## Context

The SignalR channel server delivered every finished tool call to the chat client twice. It wrote
the call into the topic's stream, and it pushed the same text over the hub as well —
`OnToolCalls` for an auto-approved call, and the `ToolCalls` field on `OnApprovalResolved` for
one the user approved.

The two copies did not agree. The stream copy for an approved call carried no message id at all,
because `RequestApprovalAsync` wrote it unlabelled; the push carried the id of the message the
call belonged to. So the client received the same call twice under two different labels, in an
order neither side controlled.

`TopicStreams` grew about eighty lines to reconcile that: a per-message memory of what a push had
shown, a rule for gluing a pushed call onto whatever message was being written, a step in the
turn boundary that moved the call to the message it named once the reply got there, and a
suppressor that dropped a block already ending the accumulator. The suppressor could not tell a
second delivery from a genuine repeat, and the code said so — two identical calls with nothing
between them showed as one.

The push bought nothing the stream did not already deliver. A pushed call is applied only to a
reply the client is already reading, and a client reading a reply is a client subscribed to the
topic's stream, so the push reached no browser the stream missed. It was also the weaker of the
two routes: the stream goes through `StreamBuffer`, so a reload replays it, while a push is gone.

## Decision

**A tool call reaches the browser on the topic's stream, and nowhere else.**

`OnToolCalls` is deleted. `OnApprovalResolved` keeps only what a browser cannot get from the
stream — that the prompt is resolved and should come off screen.

Both approval paths write the call into the stream the same way, grouped by the message id it
belongs to, so an approved call is labelled exactly as an auto-approved one already was.

The client's reconciliation goes with the second route: the topic-keyed `Append` verb, the pushed
tool-call memory, the glue-and-move step in the turn boundary and the second-delivery suppressor
are all deleted. A tool call is now an ordinary chunk of the reply.

## Considered options

**Keep both routes and make them agree.** Label the stream copy with its message id and leave the
push in place, so the suppressor could match on id rather than on text. It would have fixed the
lost-repeat behaviour. Rejected because it keeps two writers for one fact and a suppressor whose
correctness depends on their ordering, which nothing enforces.

**Drop the stream copy and keep the push.** Fewer moving parts on the server. Rejected because
the push does not survive a reload and does not arrive in order with the reply, so a resumed
conversation would lose its tool calls and a live one would show them at the wrong point.

## Consequences

- Two identical tool calls in a row now show twice, correctly. That was the behaviour the
  suppressor traded away.
- A tool call is buffered and replayed on resume like the rest of the reply, on both approval
  paths. The approved-call copy used to be unlabelled, so a resume attached it to whatever the
  buffer had last.
- `ApprovalResolvedNotification` and `ApprovalResolved` are wire and action shapes with one job.
  The `ToolCalls` field on the action was already unread by the reducer.
- Server and client ship from this repository and deploy together, so the removed push needs no
  compatibility window. A client from before this change would stop showing tool calls it had
  only ever been shown twice.
