# Spec — One Turn, One Conversation Id

Status: ready-for-agent

## Problem Statement

A scheduled agent fires, does its work, and answers in WebChat. On the dashboard that turn shows up as two turns. Half of it — the tool calls, the tool execution latency, the time to first token, the total LLM time, the history write — is filed under a synthetic id like `sched-morning-news-12345`, which is not a conversation anybody can open. The other half — the first-reply latency — is filed under the WebChat conversation the user is actually reading. No single row shows the whole turn.

The reason is that one concept has three names inside the same method. The group key, built from the message's own conversation id, is what the agent is constructed from. The persistence key, built from the first delivery target, is what chat history restores under. The delivery target's id is what the first-reply latency is attributed to. For every interactive message the three are the same string, so nothing looks wrong. For a scheduled fire that mints a fresh WebChat conversation they diverge, and every downstream consumer picks whichever one happened to be in scope where it was written.

There is a comment in the code that reasons carefully about which id the first-reply latency belongs to, and concludes: the one where the reply actually landed. That reasoning is correct and it applies just as well to the other six events. It was never applied to them, because nothing in the code makes the choice once.

A fourth name is hidden a level down. Memory recall is handed the message's own conversation id, so a scheduled turn's recall metrics — and the provenance stamped on any memory extracted from that turn — point at the synthetic scheduling id. The memory records that provenance durably. Six months later it names a conversation that cannot be opened.

The approval handler makes the divergence invisible. It is a two-method adapter that carries a conversation id privately and forwards to the channel. Because the id is private, the fact that approvals were routed to one conversation while metrics were stamped with another is not visible at any call site.

## Solution

Resolve the identity once, where the delivery targets are already resolved, and use that one value everywhere downstream.

The group anchors already compute it: the first delivery target's conversation id, falling back to the message's own when there are no targets. That value already keys chat history. It becomes the single delivery identity for the turn — the agent is built from it, the thread restores under it, approvals route to it, and every event the turn publishes is stamped with it.

The grouping key stays what it is. Messages are still grouped by the conversation they arrived on, and the thread resolver still keys cancellation and clear on that. Grouping and identity are genuinely two things; the change is that identity stops being three.

The approval adapter is deleted. Its two methods have the same shape as the two channel methods they forward to, with the conversation id curried away. Putting the conversation id back on the interface lets a channel connection be an approval handler directly, and the private string that hid the divergence has nowhere left to live.

## User Stories

1. As an operator, I want a scheduled turn to appear as one conversation on the dashboard, so that I can read what the agent did without joining two rows by hand.
2. As an operator, I want a scheduled turn's tool calls filed under the conversation the reply landed in, so that clicking a slow tool call takes me to a conversation I can open.
3. As an operator, I want the tool execution latency of a scheduled turn attributed to the same conversation as its first-reply latency, so that the two halves of one turn's latency budget are comparable.
4. As an operator, I want time-to-first-token and total LLM time for a scheduled turn filed with the rest of that turn, so that a slow model shows up against the conversation it slowed down.
5. As an operator, I want the history-write latency of a scheduled turn attributed to the conversation whose history was written, so that the event names the thing it measured.
6. As an operator, I want memory-recall latency and recall counts for a scheduled turn filed under the delivered conversation, so that recall cost is attributable to a real conversation.
7. As an operator, I want a memory extracted during a scheduled turn to record the delivered conversation as its source, so that the provenance link resolves to something I can read.
8. As an operator, I want the OpenRouter session id for a turn to match the conversation the turn belongs to, so that provider-side session grouping agrees with our own.
9. As an operator, I want the agent instance name for a scheduled turn to carry the delivered conversation id, so that logs from that run join to the same conversation as its metrics.
10. As an operator, I want an interactive WebChat turn to be unaffected by all of this, so that the fix for the scheduled case is not a change to the common case.
11. As an operator, I want a Telegram turn to be unaffected, so that channels that never mint a conversation keep the ids they always had.
12. As a user of a scheduled agent, I want the tool approvals for that run to arrive in the same WebChat conversation the answer arrives in, so that approving a tool and reading the result happen in one place.
13. As a user, I want auto-approval notices to land in the conversation I am reading, so that I can see what the agent ran without approving it.
14. As a user, I want a sub-agent's tool calls to be attributed to the conversation that spawned them, so that a sub-agent's work is not filed with a blank conversation id.
15. As a developer, I want the delivery identity computed in exactly one place, so that a new downstream consumer cannot pick the wrong id by picking whichever variable was in scope.
16. As a developer, I want the target-or-message fallback expression written once, so that three copies of it cannot drift apart.
17. As a developer, I want the agent factory to receive one identity rather than an identity and a hint, so that its five uses of that identity cannot be individually rerouted.
18. As a developer, I want the approval handler's conversation id visible at the call site, so that where approvals go is readable without opening an adapter class.
19. As a developer, I want the approval adapter class gone, so that there is no place for a fourth name to hide.
20. As a developer, I want a channel connection to satisfy the approval-handler contract directly, so that wiring an approval route is not a construction step that can be got wrong.
21. As a developer, I want the comment that reasons about first-reply attribution to describe the general rule, so that the next person does not think the reasoning was specific to one event.
22. As a developer adding a new metric to a turn, I want one obvious identity to stamp it with, so that adding an event does not require rediscovering this problem.
23. As a developer, I want the scheduled-mint case covered by a fast unit test, so that a regression is a red test rather than a dashboard anomaly noticed weeks later.
24. As a developer, I want the interactive case covered alongside it, so that the fix cannot silently reroute the common path.

## Implementation Decisions

### One identity, carried as the key that already exists

The group anchors already hold an `AgentKey` built from the first delivery target. It is named for its narrowest use — chat-history persistence — and is only used for that. It is renamed to reflect what it actually is: the turn's delivery identity. It then carries three jobs instead of one.

```csharp
var anchors = await ResolveGroupAnchorsAsync(first, agentKey, ct);
await using var agent = agentFactory.Create(anchors.DeliveryKey, ...);
var context = threadResolver.Resolve(agentKey);   // grouping key stays
var thread = await GetOrRestoreThread(agent, anchors.DeliveryKey, ct);
```

This was chosen over adding a separate conversation-id string to the anchors and widening the factory interface to take both. The key already exists, already holds the right value, and is already the type the factory takes. Passing it to `Create` changes no interface and changes no line inside the agent factory: all five of its uses of that key's conversation id — the OpenRouter session id, the agent instance name, the approval chat client's metric id, the agent's own conversation id, and through it the history-write latency id — follow automatically. Two identity parameters travelling together would have been a new opportunity for them to disagree.

The fallback stays exactly as today: when there are no delivery targets, the delivery identity is the message's own conversation id. Every channel that does not mint keeps the ids it has now.

### The fallback expression is written once

Three places compute "the first delivery target's conversation id, or the message's own": the approval binding, the persistence key, and the conversation context handed to the LLM. A fourth computes the same thing for first-reply latency from the per-turn targets. The group-level resolution computes it once and the anchors carry it.

### First-reply attribution follows the group identity

First-reply latency is attributed today to the per-turn delivery targets. Under one identity it is attributed to the turn group's delivery identity, like everything else.

This is a real behaviour change in one narrow case: a later message arriving from a different channel into an existing group — a user typing in WebChat inside a voice-started conversation — currently has its first-reply latency attributed to its own channel's target. It will now be attributed to the group's delivery identity. That is the correct outcome, because every other event that message produces is already stamped with the group identity: the agent is constructed once per group, so its tool-call and LLM events cannot be per-message. Making first reply agree with them is the point of the work.

### The approval adapter is deleted

The conversation id moves onto the approval-handler contract, which then has exactly the shape the channel connection already has:

```csharp
public interface IToolApprovalHandler
{
    Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct);

    Task NotifyAutoApprovedAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct);
}

public interface IChannelConnection : IToolApprovalHandler { ... }
```

The channel connection interface declares the two methods itself today with identical signatures; it now inherits them. The adapter class, the approval-handler factory delegate on the chat monitor, and the DI registration that supplied it all go. The monitor passes the approval channel itself.

The approval chat client already holds a conversation id for stamping metrics. That id becomes the one it passes on every approval and auto-approval call, and it becomes required rather than optional. One string, one meaning, visible where the client is constructed.

### Sub-agents carry the parent's identity

The approval chat client wrapping a sub-agent is constructed today with no conversation id, so sub-agent tool-call and tool-execution events are published with a null conversation id. With the id required for the approval call, the sub-agent factory method takes the parent turn's delivery conversation id and passes it down, including through its own recursion for nested sub-agents.

This fixes the null attribution as a consequence. It is in scope because the alternative is a null conversation id in the one place the work is about.

### Memory recall joins the unification

The memory recall hook is handed the delivery identity's conversation id rather than the message's own. This moves the recall event, the recall latency event, and the source provenance recorded on any memory extracted from the turn.

The provenance is durable data. Existing memory records are not migrated; memories written from a scheduled turn before this change keep pointing at a synthetic scheduling id, and memories written after point at a real conversation. This is accepted for the same reason the metric split is accepted below.

### The grouping key keeps its job

Messages are grouped by the arriving conversation, and the thread resolver's context, cancel and clear all stay keyed on that group key. Nothing about cancellation or the clear command changes. The delivery identity is the downstream identity only.

### The attribution comment

The comment explaining that an event belongs to where the reply landed is rewritten to state the general rule and moved to where the identity is resolved, since that is now the one place the decision is made.

## Testing Decisions

A good test here asserts what an operator would see: for a scheduled fire that mints a WebChat conversation, everything the turn produces names the minted conversation. It does not assert which private field holds the id, and it does not assert that a particular method was called with a particular argument where a behaviour is observable instead.

**One seam, in the chat monitor's own unit tests.** The existing persistence-key unit file is the seam. It is small, it already builds the exact fixture this work needs — a schedule fire whose reply target has a null conversation id, prompting the fake WebChat channel to mint `7:9` — and it already asserts one consequence of the delivery identity. It becomes the file that asserts all of them, and is renamed for the broader subject.

Four assertions on the scheduled-mint fire: the agent is constructed with the minted id, the thread restores under the minted id, the approval route binds to the minted channel and id, and the first-reply latency event carries the minted id. Its existing sibling test — a plain WebChat message keeping its own conversation id — stays as the regression net for the common path.

The fake agent factory in the unit test fixtures does not record the key it was handed; it gains that. The fake channel connection already records auto-approval calls with their conversation id, which is what makes the approval assertion observable without a mock.

**No new infrastructure tests for the downstream uses.** Because the delivery identity is passed as the key the agent factory already takes, no line inside the agent factory changes. Its existing tests cover it. Asserting the session id and agent name against the key would mean widening the injected chat-client factory delegate, which currently drops the session id before the test can see it — new production surface to serve a test, for a wiring that the compiler already guarantees.

**The approval contract change is covered by the tests that already exist.** Every fake and mock approval handler in the unit and integration suites gains the parameter. The approval chat client's own unit files — which are extensive and already cover the whitelist, dynamic approval, rejection and metrics paths — keep passing, and that is the statement that the contract change moved no behaviour.

**Memory recall.** The recall hook's existing tests assert on the conversation id it stamps. The chat monitor test asserts the hook receives the delivery identity; the hook's own tests already cover what it does with what it receives.

## Out of Scope

Migrating existing metric or memory rows. A scheduled conversation's history straddles the change: events published before it carry the synthetic id, events after carry the delivered id. Nothing rewrites the old rows. The dashboard's latency page displays and sorts by the conversation id as a plain string and treats a missing one as a dash, so it cannot error on a conversation whose events span two ids; the pre-change events for each scheduled agent simply keep listing under the synthetic id. Accepted.

Restoring prompt-cache continuity for scheduled agents. See the accepted trade below.

Changing how messages are grouped. Grouping by the arriving conversation id is correct and stays.

Changing where chat history is keyed. It already uses the delivery identity, via the same key this work generalises. Do not "fix" it onto the group key while unifying — that would break WebChat's view of a minted conversation, which is the bug the persistence key was introduced to fix.

The conversation context handed to the LLM. It already uses the delivery target's id; it is one of the three copies of the fallback expression and is folded into the single resolution, but its value does not change.

## Further Notes

**The accepted trade: scheduled agents lose the prompt cache.** The OpenRouter session id is one of the five uses that follow the delivery identity, and a scheduled fire mints a fresh conversation on every run. So a scheduled agent's session id changes every fire, and every scheduled turn becomes a full prompt-cache miss on the static prefix.

This was raised twice and accepted twice. The alternative — deriving the session id from the stable group key while everything observable used the delivery identity — was offered and declined, in favour of one concept with no exceptions. It is recorded in project memory as `scheduled-agents-accept-cache-miss` precisely so that a later session does not read it as a regression and quietly revert it. If the cache cost becomes a real problem, reopen it as a deliberate trade, not as a bug.

**Corrections to the plan's factual claims, verified against the code.** Every site the plan cites is accurate. Two things it did not have:

The memory recall hook is a fourth name for the same concept, living inside the chat monitor itself, and it writes durable provenance rather than only telemetry. The plan's event list did not include it. It is in scope by the decision above.

The history-write latency event reaches its conversation id through the agent's conversation id, not through the persistence key — the persistence key seeds the session state bag, which is where the actual storage key comes from. Both facts are consistent with the plan's risk note that history keying does not change, but the two must not be conflated while editing: one is a metric label that moves, the other is a storage key that does not.
