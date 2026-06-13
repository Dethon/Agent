# WebChat Refresh — Plan B: Cross-Agent Activity State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide the data layer the Hearth navigation (Plan C) consumes: a single shared unread/streaming selector (lifted out of `TopicList`), plus a bounded client-side slice that maps every space agent's topics so a *background* agent's activity (it's streaming now, or just finished) can be surfaced before you switch to it.

**Architecture:** Reuses the existing Redux-like pattern — immutable `record` state, static `Reduce` switch expressions, `Store<TState>` over a `BehaviorSubject`, `Dispatcher.RegisterHandler`, effects registered in DI and eagerly resolved in `Program.cs`. We add one slice (`AgentActivity`) plus pure selectors. All new logic is pure C# and unit-tested with xUnit + Shouldly (there is **no bUnit**; this plan is deliberately the test-rich one).

**Tech Stack:** Blazor WebAssembly, System.Reactive, xUnit + Shouldly.

**Scope note:** Plan B of three. Depends on Plan A (foundations). Plan C (The Hearth) consumes the selectors/store created here. The bounded exception approved in the spec (§2): a new client-side effect loads *lightweight topic metadata* (topic→agent mapping, no message history) for all 2–4 space agents at startup. No backend/protocol/persisted change.

---

## File Structure

| File | Change | Responsibility |
|------|--------|----------------|
| `WebChat.Client/State/Topics/UnreadSelectors.cs` | Create | Pure unread/streaming fold lifted verbatim from `TopicList` |
| `WebChat.Client/Components/TopicList.razor` | Modify | Delegate to `UnreadSelectors` (remove the inline copies) |
| `WebChat.Client/State/AgentActivity/AgentActivityState.cs` | Create | `TopicToAgent` map + `AgentsWithUnseenActivity` set |
| `WebChat.Client/State/AgentActivity/AgentActivityActions.cs` | Create | `AllAgentsTopicsMapped`, `MarkAgentUnseenActivity`, `ClearAgentUnseenActivity` |
| `WebChat.Client/State/AgentActivity/AgentActivityReducers.cs` | Create | Static `Reduce` |
| `WebChat.Client/State/AgentActivity/AgentActivityStore.cs` | Create | Store + handler registration |
| `WebChat.Client/State/AgentActivity/AgentActivitySelectors.cs` | Create | Pure folds: streaming agents, agents-with-activity |
| `WebChat.Client/State/Effects/AgentActivityEffect.cs` | Create | Load all-agents mappings; mark/clear unseen activity |
| `WebChat.Client/Extensions/ServiceCollectionExtensions.cs` | Modify | Register store + effect |
| `WebChat.Client/Program.cs` | Modify | Eagerly resolve the effect |
| `Tests/Unit/WebChat.Client/State/UnreadSelectorsTests.cs` | Create | Unread fold behavior |
| `Tests/Unit/WebChat.Client/State/AgentActivityReducersTests.cs` | Create | Reducer behavior |
| `Tests/Unit/WebChat.Client/State/AgentActivitySelectorsTests.cs` | Create | Selector folds |

---

### Task 1: Extract the shared unread selector

Lift `ComputeUnreadCounts`/`GetUnreadCountSince` out of `TopicList.razor` (where they're private and untested) into a pure static class, add tests, then delegate.

**Files:**
- Create: `WebChat.Client/State/Topics/UnreadSelectors.cs`
- Test: `Tests/Unit/WebChat.Client/State/UnreadSelectorsTests.cs`
- Modify: `WebChat.Client/Components/TopicList.razor`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/WebChat.Client/State/UnreadSelectorsTests.cs`:

```csharp
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class UnreadSelectorsTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topics;
    private readonly MessagesStore _messages;
    private readonly StreamingStore _streaming;

    public UnreadSelectorsTests()
    {
        _topics = new TopicsStore(_dispatcher);
        _messages = new MessagesStore(_dispatcher);
        _streaming = new StreamingStore(_dispatcher);
    }

    public void Dispose()
    {
        _topics.Dispose();
        _messages.Dispose();
        _streaming.Dispose();
    }

    private static StoredTopic Topic(string id, string? lastRead) =>
        new() { TopicId = id, AgentId = "a1", Name = id, LastReadMessageId = lastRead };

    private static ChatMessageModel Msg(string id, string role = "assistant") =>
        new() { Role = role, MessageId = id };

    [Fact]
    public void CountsMessagesAfterLastRead_ForUnselectedTopic()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", "m1")]));
        _dispatcher.Dispatch(new MessagesLoaded("t1", [Msg("m1"), Msg("m2"), Msg("m3")]));

        var counts = UnreadSelectors.ComputeUnreadCounts(_messages.State, _topics.State, _streaming.State);

        counts["t1"].ShouldBe(2);
    }

    [Fact]
    public void SelectedTopic_IsNeverUnread()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", "m1")]));
        _dispatcher.Dispatch(new MessagesLoaded("t1", [Msg("m1"), Msg("m2")]));
        _dispatcher.Dispatch(new SelectTopic("t1"));

        var counts = UnreadSelectors.ComputeUnreadCounts(_messages.State, _topics.State, _streaming.State);

        counts.ContainsKey("t1").ShouldBeFalse();
    }

    [Fact]
    public void NullLastRead_CountsAllMessages()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", null)]));
        _dispatcher.Dispatch(new MessagesLoaded("t1", [Msg("m1"), Msg("m2")]));

        var counts = UnreadSelectors.ComputeUnreadCounts(_messages.State, _topics.State, _streaming.State);

        counts["t1"].ShouldBe(2);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UnreadSelectorsTests"`
Expected: FAIL — `UnreadSelectors` does not exist (compile error).

- [ ] **Step 3: Create `UnreadSelectors` (verbatim lift to preserve behavior)**

Create `WebChat.Client/State/Topics/UnreadSelectors.cs`:

```csharp
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State.Topics;

// Lifted verbatim from TopicList to a single shared, testable source of truth.
// Behavior is intentionally unchanged (it touches the fragile streaming bookkeeping).
public static class UnreadSelectors
{
    public static IReadOnlyDictionary<string, int> ComputeUnreadCounts(
        MessagesState messagesState,
        TopicsState topicsState,
        StreamingState streamingState)
    {
        var result = new Dictionary<string, int>();
        foreach (var topic in topicsState.Topics)
        {
            if (topic.TopicId == topicsState.SelectedTopicId) continue;

            var messages = messagesState.MessagesByTopic.GetValueOrDefault(topic.TopicId, []);
            var hasStreamingContent = streamingState.StreamingByTopic.TryGetValue(topic.TopicId, out var streaming)
                                      && streaming.HasContent;

            if (messages.Count == 0 && !hasStreamingContent) continue;

            var hasStreamingMessageId = hasStreamingContent && streaming?.CurrentMessageId is not null;
            List<ChatMessageModel> allMessages = hasStreamingMessageId
                ? [.. messages, new ChatMessageModel { Role = "assistant", MessageId = streaming?.CurrentMessageId }]
                : [.. messages];

            var unreadCount = GetUnreadCountSince(allMessages, topic.LastReadMessageId);
            if (unreadCount > 0)
            {
                result[topic.TopicId] = unreadCount;
            }
        }
        return result;
    }

    public static int GetUnreadCountSince(IReadOnlyList<ChatMessageModel> messages, string? lastReadMessageId)
    {
        if (lastReadMessageId is null) return messages.Count;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].MessageId == lastReadMessageId)
            {
                return messages.Count - 1 - i;
            }
        }

        return messages.Count;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UnreadSelectorsTests"`
Expected: PASS (3 facts).

- [ ] **Step 5: Delegate from `TopicList.razor`**

In `TopicList.razor`, the `Subscribe(...)` calls reference `ComputeUnreadCounts(...)`. Replace each `ComputeUnreadCounts(...)` call with `UnreadSelectors.ComputeUnreadCounts(...)`, then **delete** the two private methods `ComputeUnreadCounts` and `GetUnreadCountSince` from the `@code` block. Add `@using WebChat.Client.State.Topics` at the top if not already imported (it is the same namespace family; verify the call resolves). Build to confirm:

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds (no duplicate-definition or missing-method errors).

- [ ] **Step 6: Commit**

```bash
git add WebChat.Client/State/Topics/UnreadSelectors.cs WebChat.Client/Components/TopicList.razor Tests/Unit/WebChat.Client/State/UnreadSelectorsTests.cs
git commit -m "refactor(webchat): extract shared UnreadSelectors from TopicList"
```

---

### Task 2: AgentActivity slice — state, actions, reducer

**Files:**
- Create: `WebChat.Client/State/AgentActivity/AgentActivityState.cs`, `AgentActivityActions.cs`, `AgentActivityReducers.cs`
- Test: `Tests/Unit/WebChat.Client/State/AgentActivityReducersTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/WebChat.Client/State/AgentActivityReducersTests.cs`:

```csharp
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.AgentActivity;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentActivityReducersTests
{
    [Fact]
    public void AllAgentsTopicsMapped_SetsTopicToAgentMap()
    {
        var state = AgentActivityState.Initial;
        var mapped = new Dictionary<string, string> { ["t1"] = "a1", ["t2"] = "a2" };

        var next = AgentActivityReducers.Reduce(state, new AllAgentsTopicsMapped(mapped));

        next.TopicToAgent["t1"].ShouldBe("a1");
        next.TopicToAgent["t2"].ShouldBe("a2");
    }

    [Fact]
    public void MarkAgentUnseenActivity_AddsAgent()
    {
        var next = AgentActivityReducers.Reduce(AgentActivityState.Initial, new MarkAgentUnseenActivity("a2"));

        next.AgentsWithUnseenActivity.ShouldContain("a2");
    }

    [Fact]
    public void ClearAgentUnseenActivity_RemovesAgent()
    {
        var seeded = AgentActivityReducers.Reduce(AgentActivityState.Initial, new MarkAgentUnseenActivity("a2"));

        var next = AgentActivityReducers.Reduce(seeded, new ClearAgentUnseenActivity("a2"));

        next.AgentsWithUnseenActivity.ShouldNotContain("a2");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentActivityReducersTests"`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3: Create the state**

Create `WebChat.Client/State/AgentActivity/AgentActivityState.cs`:

```csharp
using System.Collections.Immutable;

namespace WebChat.Client.State.AgentActivity;

public sealed record AgentActivityState
{
    public ImmutableDictionary<string, string> TopicToAgent { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    public ImmutableHashSet<string> AgentsWithUnseenActivity { get; init; } = [];

    public static AgentActivityState Initial => new();
}
```

- [ ] **Step 4: Create the actions**

Create `WebChat.Client/State/AgentActivity/AgentActivityActions.cs`:

```csharp
namespace WebChat.Client.State.AgentActivity;

public record AllAgentsTopicsMapped(IReadOnlyDictionary<string, string> TopicToAgent) : IAction;

public record MarkAgentUnseenActivity(string AgentId) : IAction;

public record ClearAgentUnseenActivity(string AgentId) : IAction;
```

- [ ] **Step 5: Create the reducer**

Create `WebChat.Client/State/AgentActivity/AgentActivityReducers.cs`:

```csharp
using System.Collections.Immutable;

namespace WebChat.Client.State.AgentActivity;

public static class AgentActivityReducers
{
    public static AgentActivityState Reduce(AgentActivityState state, IAction action) => action switch
    {
        AllAgentsTopicsMapped a => state with
        {
            TopicToAgent = a.TopicToAgent.ToImmutableDictionary()
        },

        MarkAgentUnseenActivity a => state with
        {
            AgentsWithUnseenActivity = state.AgentsWithUnseenActivity.Add(a.AgentId)
        },

        ClearAgentUnseenActivity a => state with
        {
            AgentsWithUnseenActivity = state.AgentsWithUnseenActivity.Remove(a.AgentId)
        },

        _ => state
    };
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentActivityReducersTests"`
Expected: PASS (3 facts).

- [ ] **Step 7: Commit**

```bash
git add WebChat.Client/State/AgentActivity/AgentActivityState.cs WebChat.Client/State/AgentActivity/AgentActivityActions.cs WebChat.Client/State/AgentActivity/AgentActivityReducers.cs Tests/Unit/WebChat.Client/State/AgentActivityReducersTests.cs
git commit -m "feat(webchat): AgentActivity slice (state, actions, reducer)"
```

---

### Task 3: AgentActivity selectors (the per-agent fold)

**Files:**
- Create: `WebChat.Client/State/AgentActivity/AgentActivitySelectors.cs`
- Test: `Tests/Unit/WebChat.Client/State/AgentActivitySelectorsTests.cs`

`GetActiveAgentIds` folds live streaming topics through the topic→agent map; `GetAgentsWithActivity` unions that with the persisted unseen-activity set. These drive the Hearth's per-agent dots in Plan C.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/WebChat.Client/State/AgentActivitySelectorsTests.cs`:

```csharp
using System.Collections.Immutable;
using Shouldly;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentActivitySelectorsTests
{
    private static AgentActivityState WithMap(params (string topic, string agent)[] pairs) =>
        AgentActivityState.Initial with
        {
            TopicToAgent = pairs.ToImmutableDictionary(p => p.topic, p => p.agent)
        };

    [Fact]
    public void GetActiveAgentIds_MapsStreamingTopicsToTheirAgents()
    {
        var state = WithMap(("t1", "a1"), ("t2", "a2"), ("t3", "a2"));
        var streaming = StreamingState.Initial with { StreamingTopics = ["t2"] };

        var active = AgentActivitySelectors.GetActiveAgentIds(state, streaming);

        active.ShouldContain("a2");
        active.ShouldNotContain("a1");
    }

    [Fact]
    public void GetAgentsWithActivity_UnionsStreamingAndUnseen()
    {
        var state = WithMap(("t1", "a1")) with { AgentsWithUnseenActivity = ["a3"] };
        var streaming = StreamingState.Initial with { StreamingTopics = ["t1"] };

        var activity = AgentActivitySelectors.GetAgentsWithActivity(state, streaming);

        activity.ShouldBe(new HashSet<string> { "a1", "a3" }, ignoreOrder: true);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentActivitySelectorsTests"`
Expected: FAIL — `AgentActivitySelectors` does not exist.

- [ ] **Step 3: Create the selectors**

Create `WebChat.Client/State/AgentActivity/AgentActivitySelectors.cs`:

```csharp
using System.Collections.Immutable;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State.AgentActivity;

public static class AgentActivitySelectors
{
    public static IReadOnlySet<string> GetActiveAgentIds(AgentActivityState state, StreamingState streaming) =>
        streaming.StreamingTopics
            .Select(topicId => state.TopicToAgent.GetValueOrDefault(topicId))
            .Where(agentId => agentId is not null)
            .Select(agentId => agentId!)
            .ToImmutableHashSet();

    public static IReadOnlySet<string> GetAgentsWithActivity(AgentActivityState state, StreamingState streaming) =>
        GetActiveAgentIds(state, streaming)
            .Union(state.AgentsWithUnseenActivity)
            .ToImmutableHashSet();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentActivitySelectorsTests"`
Expected: PASS (2 facts).

- [ ] **Step 5: Commit**

```bash
git add WebChat.Client/State/AgentActivity/AgentActivitySelectors.cs Tests/Unit/WebChat.Client/State/AgentActivitySelectorsTests.cs
git commit -m "feat(webchat): AgentActivity selectors (per-agent streaming/activity fold)"
```

---

### Task 4: AgentActivityStore

**Files:**
- Create: `WebChat.Client/State/AgentActivity/AgentActivityStore.cs`

Mirrors `TopicsStore` exactly: wraps `Store<AgentActivityState>`, registers a handler per action.

- [ ] **Step 1: Create the store**

Create `WebChat.Client/State/AgentActivity/AgentActivityStore.cs`:

```csharp
namespace WebChat.Client.State.AgentActivity;

public sealed class AgentActivityStore : IDisposable
{
    private readonly Store<AgentActivityState> _store;

    public AgentActivityStore(Dispatcher dispatcher)
    {
        _store = new Store<AgentActivityState>(AgentActivityState.Initial);

        dispatcher.RegisterHandler<AllAgentsTopicsMapped>(action =>
            _store.Dispatch(action, AgentActivityReducers.Reduce));
        dispatcher.RegisterHandler<MarkAgentUnseenActivity>(action =>
            _store.Dispatch(action, AgentActivityReducers.Reduce));
        dispatcher.RegisterHandler<ClearAgentUnseenActivity>(action =>
            _store.Dispatch(action, AgentActivityReducers.Reduce));
    }

    public AgentActivityState State => _store.State;
    public IObservable<AgentActivityState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add WebChat.Client/State/AgentActivity/AgentActivityStore.cs
git commit -m "feat(webchat): AgentActivityStore"
```

---

### Task 5: AgentActivityEffect + DI wiring

**Files:**
- Create: `WebChat.Client/State/Effects/AgentActivityEffect.cs`
- Modify: `WebChat.Client/Extensions/ServiceCollectionExtensions.cs`, `WebChat.Client/Program.cs`

The effect (a) on `SetAgents`, loads each agent's topic list (lightweight — only to map `topicId → agentId`) and dispatches `AllAgentsTopicsMapped`; (b) clears unseen activity for an agent when it's selected; (c) watches streaming transitions and, when a *non-selected* agent's topic finishes streaming, marks it unseen. The async/service parts are verified by build + manual/integration; the pure reducer and selectors are already covered by Tasks 2–3.

- [ ] **Step 1: Create the effect**

Create `WebChat.Client/State/Effects/AgentActivityEffect.cs`. The constructor dependencies mirror `AgentSelectionEffect` (which already injects `Dispatcher`, `TopicsStore`, `ITopicService`, `SpaceStore`); add `StreamingStore` and `AgentActivityStore`:

```csharp
using System.Collections.Immutable;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentActivityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly AgentActivityStore _activityStore;
    private readonly ITopicService _topicService;
    private readonly SpaceStore _spaceStore;
    private readonly IDisposable _streamingSubscription;
    private readonly IDisposable _setAgentsRegistration;
    private readonly IDisposable _selectAgentRegistration;
    private ImmutableHashSet<string> _previousStreamingTopics = [];

    public AgentActivityEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        AgentActivityStore activityStore,
        ITopicService topicService,
        SpaceStore spaceStore)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _activityStore = activityStore;
        _topicService = topicService;
        _spaceStore = spaceStore;

        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(HandleSetAgents);
        _selectAgentRegistration = dispatcher.RegisterHandler<SelectAgent>(
            action => _dispatcher.Dispatch(new ClearAgentUnseenActivity(action.AgentId)));
        _streamingSubscription = streamingStore.StateObservable.Subscribe(HandleStreamingChange);
    }

    private void HandleSetAgents(SetAgents action) => _ = MapAllAgentTopicsAsync(action.Agents);

    private async Task MapAllAgentTopicsAsync(IReadOnlyList<Domain.DTOs.Channel.AgentCatalogEntry> agents)
    {
        var slug = _spaceStore.State.CurrentSlug;
        var map = new Dictionary<string, string>();
        foreach (var agent in agents)
        {
            var topics = await _topicService.GetAllTopicsAsync(agent.Id, slug);
            foreach (var topic in topics)
            {
                map[topic.TopicId] = agent.Id;
            }
        }
        _dispatcher.Dispatch(new AllAgentsTopicsMapped(map));
    }

    private void HandleStreamingChange(StreamingState state)
    {
        // Topics that just stopped streaming since the last snapshot.
        var completed = _previousStreamingTopics.Except(state.StreamingTopics);
        var selectedAgent = _topicsStore.State.SelectedAgentId;
        var map = _activityStore.State.TopicToAgent;

        foreach (var topicId in completed)
        {
            if (map.TryGetValue(topicId, out var agentId) && agentId != selectedAgent)
            {
                _dispatcher.Dispatch(new MarkAgentUnseenActivity(agentId));
            }
        }

        _previousStreamingTopics = state.StreamingTopics;
    }

    public void Dispose()
    {
        _streamingSubscription.Dispose();
        _setAgentsRegistration.Dispose();
        _selectAgentRegistration.Dispose();
    }
}
```

> Note: `ITopicService.GetAllTopicsAsync(agentId, slug)` returns `TopicMetadata` (each has `TopicId` + `AgentId`); confirm the exact namespace of `AgentCatalogEntry`/`ITopicService` against `AgentSelectionEffect.cs` and adjust the `using`s if needed (the action already carries `IReadOnlyList<AgentCatalogEntry>`).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds. (Fix any `using` mismatch for `AgentCatalogEntry`/`ITopicService`/`SpaceStore` by copying the exact imports used in `AgentSelectionEffect.cs`.)

- [ ] **Step 3: Register the store and effect**

In `WebChat.Client/Extensions/ServiceCollectionExtensions.cs`:
- Add `using WebChat.Client.State.AgentActivity;` to the top usings.
- In `AddWebChatStores()`, after `services.AddScoped<SpaceStore>();` add:

```csharp
            services.AddScoped<AgentActivityStore>();
```

- In `AddWebChatEffects()`, after `services.AddScoped<SpaceEffect>();` add:

```csharp
            services.AddScoped<AgentActivityEffect>();
```

- [ ] **Step 4: Eagerly resolve the effect at startup**

In `WebChat.Client/Program.cs`, after `_ = app.Services.GetRequiredService<SpaceEffect>();` (line ~60) add:

```csharp
_ = app.Services.GetRequiredService<AgentActivityEffect>();
```

- [ ] **Step 5: Build and run the full new-unit suite**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds.

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentActivity|FullyQualifiedName~UnreadSelectorsTests"`
Expected: PASS.

- [ ] **Step 6: Manual verification**

Run the app with ≥2 agents configured. Trigger activity on a non-selected agent (e.g. a scheduled message, or send to it then switch away). Confirm via browser console that `AgentActivityStore` state populates `TopicToAgent` for all agents and that `AgentsWithUnseenActivity` gains the background agent's id after it streams, and clears when you select that agent. (The visible dot is wired in Plan C.)

- [ ] **Step 7: Commit**

```bash
git add WebChat.Client/State/Effects/AgentActivityEffect.cs WebChat.Client/Extensions/ServiceCollectionExtensions.cs WebChat.Client/Program.cs
git commit -m "feat(webchat): AgentActivityEffect — map all-agent topics, track background activity"
```

---

## Self-Review

- **Spec coverage (Plan B scope = spec §2 bounded exception, §5.3 per-agent dots, §5.4 coarse background signal, §6.2 shared selector + activity fold):** shared unread selector (Task 1), activity slice (Task 2), per-agent fold selectors (Task 3), store (Task 4), all-agents metadata effect + DI (Task 5). Covered.
- **Placeholder scan:** none — every type, action, and method is defined in-plan; the one cross-reference (`ITopicService`/`AgentCatalogEntry` namespaces) points the implementer at `AgentSelectionEffect.cs` for the exact imports, which is a real, locatable source, not a TODO.
- **Type consistency:** `AllAgentsTopicsMapped(IReadOnlyDictionary<string,string>)`, `MarkAgentUnseenActivity(string)`, `ClearAgentUnseenActivity(string)` are spelled identically in actions, reducer, store, effect, and tests; `AgentActivityState.TopicToAgent`/`AgentsWithUnseenActivity` consistent across state, reducer, selectors, tests; `GetActiveAgentIds`/`GetAgentsWithActivity` signatures match between selector and tests.
- **No-bUnit constraint honored:** every `[Fact]` targets pure C# (selectors, reducer); the effect's async/streaming wiring is verified by build + manual, with its pure dependencies already unit-covered.
- **Coarse-signal honesty (spec §5.4):** background-agent unread counts are NOT claimed; the cross-agent signal is streaming (live) ∪ unseen-since-completion, both derivable without loading background message history.
