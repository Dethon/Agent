# 01 — Resolve the delivery identity once and build the turn from it

**What to build:** A scheduled agent fires, mints a fresh WebChat conversation for its answer, and every event that run produces names that conversation. Today the run is split across two ids: the tool calls, the tool-execution latency, the time to first token, the total LLM time and the history-write latency are filed under the synthetic scheduling id, while the first-reply latency is filed under the minted WebChat id. After this ticket an operator opening the dashboard sees one conversation for one turn.

The chat monitor resolves the turn's delivery identity once, where it already resolves the delivery targets, and everything downstream is built from that one value. The agent is constructed from it and the thread restores under it, which is what carries it into the OpenRouter session id, the agent instance name, the approval chat client's metric id and the agent's own conversation id — the agent factory itself needs no change, because the identity is carried as the key that factory already takes.

The group anchors' persistence key is exactly this value already, named for its narrowest use. Rename it for what it is. The expression "the first delivery target's conversation id, or the message's own when there are no targets" is currently written out three times; write it once and have the anchors carry the result. The fallback behaviour does not change, so every channel that never mints keeps the ids it has today.

First-reply latency moves from the per-turn delivery targets onto the group's delivery identity. This is a deliberate behaviour change in one narrow case — a later message arriving from a different channel into an existing group, such as a user typing in WebChat inside a voice-started conversation. Its first-reply latency will now be attributed to the group identity, which is correct: the agent is constructed once per group, so every other event that message produces is already stamped that way.

The comment that reasons about first-reply attribution is rewritten to state the general rule and moved to where the identity is resolved, since that is now the single place the decision is made.

Do not change how messages are grouped, and do not change where chat history is stored. The grouping key stays the arriving conversation id and the thread resolver keeps keying context, cancel and clear on it. The history storage key comes from the session state bag, not from the agent's conversation id — only the history-write *latency label* moves here.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A schedule fire whose reply target has a null conversation id, prompting the fake WebChat channel to mint one, constructs its agent from the minted id
- [x] The same fire restores its thread under the minted id (existing behaviour, must not regress)
- [x] The same fire publishes its first-reply latency event with the minted id
- [x] A plain WebChat message with no reply targets keeps using its own conversation id for all of the above
- [x] The target-or-message fallback is computed in exactly one place and the anchors carry the result
- [x] The group key still drives message grouping and the thread resolver's context, cancel and clear
- [x] The existing persistence-key unit file is renamed for the broader subject and holds the assertions above; the unit-test fake agent factory records the key it was handed
- [x] `dotnet test Tests/Unit` passes

## Comments

**On "the fallback is computed in exactly one place".** The three copies inside
`ChatMonitor` — the approval binding, the persistence key and the first-reply
attribution — are now one, and `GroupAnchors.DeliveryKey` carries the result.

`DeliveryTargetResolver.BuildConversationContext` was left alone. The spec counts it
as a fourth copy and says folding it in would not change its value, but that is not
quite right: it runs on the *per-turn* targets, not the group's, and it picks a
`(channelId, conversationId)` pair rather than an id. Folding it into the group
anchors would change the reply target handed to the LLM for a later message arriving
from a different channel — it would name the group's channel while the reply itself
still goes back to the message's own origin. That is a behaviour change the spec puts
out of scope, so the expression stays where it is and stays per-turn.
