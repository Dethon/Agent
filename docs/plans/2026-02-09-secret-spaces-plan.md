# Secret Spaces Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add URL-routed "spaces" that partition topics in the WebChat, with server-configured slugs, per-space accent colors, hub-side notification filtering, and a `/{slug?}` Blazor route.

**Architecture:** Each space is a named slug configured server-side. The hub validates slugs, filters Redis topic queries by space, tracks each SignalR connection's space for notification scoping, and returns accent color to the client. The client adds a `SpaceStore` + `SpaceEffect` to the Redux-like state system, passes space slug through all topic operations, and applies the accent color to the header logo.

**Tech Stack:** .NET 10, Blazor WASM, SignalR, Redis (StackExchange.Redis), Shouldly, Moq, xUnit

---

### Task 1: Add `SpaceSlug` to Domain DTO `TopicMetadata`

**Files:**
- Modify: `Domain/DTOs/WebChat/TopicMetadata.cs`

**Step 1: Add `SpaceSlug` parameter to the record**

The record currently is:
```csharp
public record TopicMetadata(
    string TopicId,
    long ChatId,
    long ThreadId,
    string AgentId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string? LastReadMessageId = null);
```

Add `SpaceSlug` as a trailing optional parameter with default `"default"`:
```csharp
public record TopicMetadata(
    string TopicId,
    long ChatId,
    long ThreadId,
    string AgentId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string? LastReadMessageId = null,
    string SpaceSlug = "default");
```

**Step 2: Run tests to verify nothing breaks**

Run: `dotnet test Tests/ --filter "TopicsStore|HubEventDispatcher|ChatHub" -v minimal`
Expected: All existing tests PASS (default value preserves backward compatibility)

**Step 3: Commit**

```bash
git add Domain/DTOs/WebChat/TopicMetadata.cs
git commit -m "feat(domain): add SpaceSlug to TopicMetadata with default value"
```

---

### Task 2: Add `SpaceSlug` to Client-Side `StoredTopic` Model

**Files:**
- Modify: `WebChat.Client/Models/StoredTopic.cs`

**Step 1: Write the failing test**

Create test in `Tests/Unit/WebChat.Client/State/TopicsStoreTests.cs` — add a test that verifies `StoredTopic.FromMetadata` preserves `SpaceSlug`:

```csharp
[Fact]
public void FromMetadata_PreservesSpaceSlug()
{
    // Arrange
    var metadata = new TopicMetadata(
        "topic-1", 123L, 456L, "agent-1", "Test",
        DateTimeOffset.UtcNow, null, null, "my-space");

    // Act
    var topic = StoredTopic.FromMetadata(metadata);

    // Assert
    topic.SpaceSlug.ShouldBe("my-space");
}

[Fact]
public void ToMetadata_PreservesSpaceSlug()
{
    // Arrange
    var topic = new StoredTopic
    {
        TopicId = "topic-1", ChatId = 123, ThreadId = 456,
        AgentId = "agent-1", Name = "Test", SpaceSlug = "my-space",
        CreatedAt = DateTime.UtcNow
    };

    // Act
    var metadata = topic.ToMetadata();

    // Assert
    metadata.SpaceSlug.ShouldBe("my-space");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FromMetadata_PreservesSpaceSlug|ToMetadata_PreservesSpaceSlug" -v minimal`
Expected: FAIL — `StoredTopic` doesn't have `SpaceSlug` property yet

**Step 3: Write minimal implementation**

In `WebChat.Client/Models/StoredTopic.cs`, add the property and update both mapping methods:

```csharp
public class StoredTopic
{
    public string TopicId { get; set; } = "";
    public long ChatId { get; set; }
    public long ThreadId { get; set; }
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "New Chat";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public string? LastReadMessageId { get; set; }
    public string SpaceSlug { get; set; } = "default";

    public static StoredTopic FromMetadata(TopicMetadata metadata)
    {
        return new StoredTopic
        {
            TopicId = metadata.TopicId,
            ChatId = metadata.ChatId,
            ThreadId = metadata.ThreadId,
            AgentId = metadata.AgentId,
            Name = metadata.Name,
            CreatedAt = metadata.CreatedAt.UtcDateTime,
            LastMessageAt = metadata.LastMessageAt?.UtcDateTime,
            LastReadMessageId = metadata.LastReadMessageId,
            SpaceSlug = metadata.SpaceSlug
        };
    }

    public TopicMetadata ToMetadata()
    {
        return new TopicMetadata(
            TopicId,
            ChatId,
            ThreadId,
            AgentId,
            Name,
            new DateTimeOffset(CreatedAt, TimeSpan.Zero),
            LastMessageAt.HasValue ? new DateTimeOffset(LastMessageAt.Value, TimeSpan.Zero) : null,
            LastReadMessageId,
            SpaceSlug);
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FromMetadata_PreservesSpaceSlug|ToMetadata_PreservesSpaceSlug" -v minimal`
Expected: PASS

**Step 5: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS

**Step 6: Commit**

```bash
git add WebChat.Client/Models/StoredTopic.cs Tests/Unit/WebChat.Client/State/TopicsStoreTests.cs
git commit -m "feat(webchat): add SpaceSlug to StoredTopic with mapping"
```

---

### Task 3: Add Space Validation to `ChatHub` and Space-Scoped Topic Queries

**Files:**
- Modify: `Agent/Hubs/ChatHub.cs`
- Modify: `Domain/Contracts/IThreadStateStore.cs`
- Modify: `Infrastructure/StateManagers/RedisThreadStateStore.cs`
- Modify: `Agent/appsettings.json`

**Step 1: Write the failing integration test for `GetAllTopics` with space slug**

Add to `Tests/Integration/WebChat/ChatHubIntegrationTests.cs`:

```csharp
[Fact]
public async Task GetAllTopics_WithSpaceSlug_ReturnsOnlyTopicsInThatSpace()
{
    // Arrange - save topics in different spaces
    var topicDefault = new TopicMetadata(
        "topic-default", 100L, 100L, "test-agent", "Default Topic",
        DateTimeOffset.UtcNow, null, null, "default");
    var topicSecret = new TopicMetadata(
        "topic-secret", 200L, 200L, "test-agent", "Secret Topic",
        DateTimeOffset.UtcNow, null, null, "secret-room");

    await _connection.InvokeAsync("SaveTopic", topicDefault, true);
    await _connection.InvokeAsync("SaveTopic", topicSecret, true);

    // Act
    var defaultTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
        "GetAllTopics", "test-agent", "default");
    var secretTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
        "GetAllTopics", "test-agent", "secret-room");

    // Assert
    defaultTopics.ShouldContain(t => t.TopicId == "topic-default");
    defaultTopics.ShouldNotContain(t => t.TopicId == "topic-secret");
    secretTopics.ShouldContain(t => t.TopicId == "topic-secret");
    secretTopics.ShouldNotContain(t => t.TopicId == "topic-default");
}

[Fact]
public async Task GetAllTopics_WithInvalidSpace_ReturnsEmpty()
{
    // Arrange - save a topic
    var topic = new TopicMetadata(
        "topic-valid", 300L, 300L, "test-agent", "Valid Topic",
        DateTimeOffset.UtcNow, null, null, "default");
    await _connection.InvokeAsync("SaveTopic", topic, true);

    // Act
    var topics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
        "GetAllTopics", "test-agent", "nonexistent-space");

    // Assert
    topics.ShouldBeEmpty();
}
```

**Note:** The `WebChatServerFixture` needs a `Spaces` configuration. Add to fixture's builder config:
```csharp
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Spaces:0:Slug"] = "default",
    ["Spaces:0:Name"] = "Main",
    ["Spaces:0:AccentColor"] = "#e94560",
    ["Spaces:1:Slug"] = "secret-room",
    ["Spaces:1:Name"] = "Secret Room",
    ["Spaces:1:AccentColor"] = "#6366f1",
});
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "GetAllTopics_WithSpaceSlug|GetAllTopics_WithInvalidSpace" -v minimal`
Expected: FAIL — `GetAllTopics` doesn't accept a `spaceSlug` parameter yet

**Step 3: Implement space-scoped topic queries**

3a. Add `SpaceConfig` record (new file `Domain/DTOs/WebChat/SpaceConfig.cs`):
```csharp
namespace Domain.DTOs.WebChat;

public record SpaceConfig(string Slug, string Name, string AccentColor);
```

3b. Update `IThreadStateStore.GetAllTopicsAsync` signature in `Domain/Contracts/IThreadStateStore.cs`:
```csharp
Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string? spaceSlug = null);
```

3c. Update `RedisThreadStateStore.GetAllTopicsAsync` in `Infrastructure/StateManagers/RedisThreadStateStore.cs`:
```csharp
public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string? spaceSlug = null)
{
    var topics = new List<TopicMetadata>();

    await foreach (var key in _server.KeysAsync(pattern: $"topic:{agentId}:*"))
    {
        var json = await _db.StringGetAsync(key);
        if (json.IsNullOrEmpty)
        {
            continue;
        }

        var topic = JsonSerializer.Deserialize<TopicMetadata>(json.ToString());
        if (topic is not null)
        {
            topics.Add(topic);
        }
    }

    var filtered = spaceSlug is not null
        ? topics.Where(t => t.SpaceSlug == spaceSlug)
        : topics;

    return filtered.OrderByDescending(t => t.LastMessageAt ?? t.CreatedAt).ToList();
}
```

3d. Update `ChatHub.GetAllTopics` in `Agent/Hubs/ChatHub.cs` to accept space slug and validate it:
```csharp
public async Task<IReadOnlyList<TopicMetadata>> GetAllTopics(string agentId, string spaceSlug = "default")
{
    if (!IsValidSpace(spaceSlug))
    {
        return [];
    }

    Context.Items["SpaceSlug"] = spaceSlug;
    return await threadStateStore.GetAllTopicsAsync(agentId, spaceSlug);
}
```

Add space validation helper and `JoinSpace` hub method to `ChatHub`:
```csharp
private bool IsValidSpace(string slug)
{
    var spaces = hubContext.Configuration.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
    return spaces.Any(s => s.Slug == slug);
}
```

Wait — `ChatHub` doesn't have access to `IConfiguration`. Inject it via the constructor. Add `IConfiguration configuration` to `ChatHub`'s primary constructor parameters and use it:
```csharp
public sealed class ChatHub(
    IAgentFactory agentFactory,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    IThreadStateStore threadStateStore,
    WebChatMessengerClient messengerClient,
    ChatThreadResolver threadResolver,
    INotifier hubNotifier,
    IConfiguration configuration) : Hub
{
    // ...
    private bool IsValidSpace(string slug)
    {
        var spaces = configuration.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
        return spaces.Any(s => s.Slug == slug);
    }
}
```

3e. Add `JoinSpace` hub method that validates slug and returns accent color (or null for invalid):
```csharp
public string? JoinSpace(string spaceSlug)
{
    var spaces = configuration.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
    var space = spaces.FirstOrDefault(s => s.Slug == spaceSlug);
    if (space is null)
    {
        return null;
    }

    Context.Items["SpaceSlug"] = spaceSlug;
    return space.AccentColor;
}
```

3f. Add `Spaces` config to `Agent/appsettings.json`:
```json
"Spaces": [
    { "Slug": "default", "Name": "Main", "AccentColor": "#e94560" }
]
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "GetAllTopics_WithSpaceSlug|GetAllTopics_WithInvalidSpace" -v minimal`
Expected: PASS

**Step 5: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS (some tests may need the `spaceSlug` parameter added to their `GetAllTopics` calls — fix any that break by using `"default"`)

**Step 6: Commit**

```bash
git add Domain/DTOs/WebChat/SpaceConfig.cs Domain/DTOs/WebChat/TopicMetadata.cs \
  Domain/Contracts/IThreadStateStore.cs Infrastructure/StateManagers/RedisThreadStateStore.cs \
  Agent/Hubs/ChatHub.cs Agent/appsettings.json \
  Tests/Integration/WebChat/ChatHubIntegrationTests.cs Tests/Integration/Fixtures/WebChatServerFixture.cs
git commit -m "feat(hub): add space-scoped topic queries with JoinSpace validation"
```

---

### Task 4: Add Space-Scoped Notifications (Server-Side Filtering)

**Files:**
- Modify: `Agent/Hubs/HubNotificationAdapter.cs`
- Modify: `Domain/Contracts/IHubNotificationSender.cs`
- Modify: `Agent/Hubs/ChatHub.cs` (ensure space is tracked in `Context.Items`)

Currently `HubNotificationAdapter` broadcasts to `Clients.All`. We need to broadcast only to connections in the correct space. The challenge: `IHubContext<ChatHub>` doesn't know which connections belong to which space. The standard SignalR approach is **Groups**.

**Step 1: Write the failing integration test**

Add to `Tests/Integration/WebChat/ChatHubIntegrationTests.cs`:

```csharp
[Fact]
public async Task TopicNotification_OnlyReceivedByConnectionInSameSpace()
{
    // Arrange - two connections in different spaces
    var connection2 = fixture.CreateHubConnection();
    await connection2.StartAsync();
    await connection2.InvokeAsync("RegisterUser", "test-user-2");

    // Join different spaces
    await _connection.InvokeAsync<string?>("JoinSpace", "default");
    await connection2.InvokeAsync<string?>("JoinSpace", "secret-room");

    var defaultNotifications = new List<TopicChangedNotification>();
    var secretNotifications = new List<TopicChangedNotification>();

    _connection.On<TopicChangedNotification>("OnTopicChanged", n => defaultNotifications.Add(n));
    connection2.On<TopicChangedNotification>("OnTopicChanged", n => secretNotifications.Add(n));

    // Act - save a topic in default space (triggers notification)
    var topic = new TopicMetadata(
        "topic-notif", 400L, 400L, "test-agent", "Notif Topic",
        DateTimeOffset.UtcNow, null, null, "default");
    await _connection.InvokeAsync("SaveTopic", topic, true);

    // Wait for notifications
    await Task.Delay(500);

    // Assert
    defaultNotifications.ShouldContain(n => n.TopicId == "topic-notif");
    secretNotifications.ShouldNotContain(n => n.TopicId == "topic-notif");

    await connection2.DisposeAsync();
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "TopicNotification_OnlyReceivedByConnectionInSameSpace" -v minimal`
Expected: FAIL — both connections receive the notification (current `Clients.All` behavior)

**Step 3: Implement SignalR Groups-based space filtering**

3a. When a client calls `JoinSpace`, add the connection to a SignalR group named `space:{slug}`:
In `ChatHub.JoinSpace`, add after setting `Context.Items["SpaceSlug"]`:
```csharp
public async Task<string?> JoinSpace(string spaceSlug)
{
    var spaces = configuration.GetSection("Spaces").Get<SpaceConfig[]>() ?? [];
    var space = spaces.FirstOrDefault(s => s.Slug == spaceSlug);
    if (space is null)
    {
        return null;
    }

    // Leave previous space group if any
    if (Context.Items.TryGetValue("SpaceSlug", out var previous) && previous is string prevSlug)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"space:{prevSlug}");
    }

    Context.Items["SpaceSlug"] = spaceSlug;
    await Groups.AddToGroupAsync(Context.ConnectionId, $"space:{spaceSlug}");
    return space.AccentColor;
}
```

3b. Also add to group in `GetAllTopics` (for backward compat — clients that call `GetAllTopics` before `JoinSpace`):
```csharp
public async Task<IReadOnlyList<TopicMetadata>> GetAllTopics(string agentId, string spaceSlug = "default")
{
    if (!IsValidSpace(spaceSlug))
    {
        return [];
    }

    // Leave previous space group if any
    if (Context.Items.TryGetValue("SpaceSlug", out var previous) && previous is string prevSlug && prevSlug != spaceSlug)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"space:{prevSlug}");
    }

    Context.Items["SpaceSlug"] = spaceSlug;
    await Groups.AddToGroupAsync(Context.ConnectionId, $"space:{spaceSlug}");
    return await threadStateStore.GetAllTopicsAsync(agentId, spaceSlug);
}
```

3c. Change `HubNotificationAdapter` to send to a group instead of all. This requires knowing the topic's space. The simplest approach: include the space slug in the notification. Add a `SpaceSlug` field to `TopicChangedNotification` and `StreamChangedNotification`.

Actually, a cleaner approach: Change `IHubNotificationSender.SendAsync` to accept an optional `groupName` parameter:

In `Domain/Contracts/IHubNotificationSender.cs`:
```csharp
public interface IHubNotificationSender
{
    Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default);
    Task SendToGroupAsync(string groupName, string methodName, object notification, CancellationToken cancellationToken = default);
}
```

In `Agent/Hubs/HubNotificationAdapter.cs`:
```csharp
public sealed class HubNotificationAdapter(IHubContext<ChatHub> hubContext) : IHubNotificationSender
{
    public async Task SendAsync(string methodName, object notification, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.All.SendAsync(methodName, notification, cancellationToken);
    }

    public async Task SendToGroupAsync(string groupName, string methodName, object notification, CancellationToken cancellationToken = default)
    {
        await hubContext.Clients.Group(groupName).SendAsync(methodName, notification, cancellationToken);
    }
}
```

3d. Update `HubNotifier` to use group-scoped sends for topic notifications. The notifier needs to know the space. The topic notifications already include `TopicMetadata` which now has `SpaceSlug`. For `TopicChangedNotification`, extract the slug from the topic. For stream/approval/tool notifications, these are tied to a topic — we need the space slug in the notification DTOs.

Add `SpaceSlug` to notifications that need scoping. In `Domain/DTOs/WebChat/HubNotification.cs`:
```csharp
public record TopicChangedNotification(
    TopicChangeType ChangeType,
    string TopicId,
    TopicMetadata? Topic = null,
    string? SpaceSlug = null);

public record StreamChangedNotification(
    StreamChangeType ChangeType,
    string TopicId,
    string? SpaceSlug = null);
```

Update `HubNotifier` to route to groups when `SpaceSlug` is present:
```csharp
public async Task NotifyTopicChangedAsync(
    TopicChangedNotification notification,
    CancellationToken cancellationToken = default)
{
    var spaceSlug = notification.SpaceSlug ?? notification.Topic?.SpaceSlug;
    if (spaceSlug is not null)
    {
        await sender.SendToGroupAsync($"space:{spaceSlug}", "OnTopicChanged", notification, cancellationToken);
    }
    else
    {
        await sender.SendAsync("OnTopicChanged", notification, cancellationToken);
    }
}

public async Task NotifyStreamChangedAsync(
    StreamChangedNotification notification,
    CancellationToken cancellationToken = default)
{
    if (notification.SpaceSlug is not null)
    {
        await sender.SendToGroupAsync($"space:{notification.SpaceSlug}", "OnStreamChanged", notification, cancellationToken);
    }
    else
    {
        await sender.SendAsync("OnStreamChanged", notification, cancellationToken);
    }
}
```

The remaining notifications (approval, tool calls, user message) are topic-specific and go to whoever is watching that topic — they can stay as `Clients.All` for now (the client already filters by selected topic).

3e. Update `ChatHub.SaveTopic` and `ChatHub.DeleteTopic` to pass `SpaceSlug` through notifications:

In `SaveTopic`: The topic metadata already has `SpaceSlug`, and `TopicChangedNotification` will pick it up from `topic.SpaceSlug`. No change needed.

In `DeleteTopic`: The `TopicChangedNotification` for delete doesn't include the topic. Add the space slug:
```csharp
public async Task DeleteTopic(string agentId, string topicId, long chatId, long threadId)
{
    var spaceSlug = Context.Items.TryGetValue("SpaceSlug", out var slug) ? slug as string : null;
    messengerClient.EndSession(topicId);

    var agentKey = new AgentKey(chatId, threadId, agentId);
    await threadStateStore.DeleteAsync(agentKey);
    await threadStateStore.DeleteTopicAsync(agentId, chatId, topicId);
    await threadResolver.ClearAsync(agentKey);

    await hubNotifier.NotifyTopicChangedAsync(
        new TopicChangedNotification(TopicChangeType.Deleted, topicId, SpaceSlug: spaceSlug));
}
```

3f. For `WebChatMessengerClient` notifications (stream changed, etc.) — the messenger client needs to pass the space slug. The simplest approach: store the space slug per session, and include it when sending notifications. This requires checking how `WebChatMessengerClient` tracks sessions and sends notifications.

Read `Infrastructure/Clients/Messaging/WebChat/WebChatSessionManager.cs` and `WebChatMessengerClient.cs` to understand session management and where stream notifications originate. The space slug should be stored when `StartSession` is called (the client calls `StartSession` after `JoinSpace`).

**Implementation note for the subagent:** The `StartSession` in `ChatHub` currently calls `messengerClient.StartSession(topicId, agentId, chatId, threadId)`. Add `spaceSlug` parameter derived from `Context.Items["SpaceSlug"]`. Then in `WebChatMessengerClient`, store `spaceSlug` per session and include it in `StreamChangedNotification` when notifying.

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "TopicNotification_OnlyReceivedByConnectionInSameSpace" -v minimal`
Expected: PASS

**Step 5: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS

**Step 6: Commit**

```bash
git add Agent/Hubs/ChatHub.cs Agent/Hubs/HubNotificationAdapter.cs \
  Domain/Contracts/IHubNotificationSender.cs Domain/DTOs/WebChat/HubNotification.cs \
  Infrastructure/Clients/Messaging/WebChat/HubNotifier.cs \
  Tests/Integration/WebChat/ChatHubIntegrationTests.cs
git commit -m "feat(hub): scope topic notifications to space groups via SignalR Groups"
```

---

### Task 5: Client-Side Space State — `SpaceStore`

**Files:**
- Create: `WebChat.Client/State/Space/SpaceState.cs`
- Create: `WebChat.Client/State/Space/SpaceActions.cs`
- Create: `WebChat.Client/State/Space/SpaceReducers.cs`
- Create: `WebChat.Client/State/Space/SpaceStore.cs`
- Create: `Tests/Unit/WebChat.Client/State/SpaceStoreTests.cs`

**Step 1: Write the failing tests**

In `Tests/Unit/WebChat.Client/State/SpaceStoreTests.cs`:
```csharp
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Space;

namespace Tests.Unit.WebChat.Client.State;

public class SpaceStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SpaceStore _store;

    public SpaceStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new SpaceStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Initial_HasDefaultSlugAndColor()
    {
        _store.State.CurrentSlug.ShouldBe("default");
        _store.State.AccentColor.ShouldBe("#e94560");
    }

    [Fact]
    public void SpaceValidated_UpdatesSlugAndAccentColor()
    {
        _dispatcher.Dispatch(new SpaceValidated("secret-room", "#6366f1"));

        _store.State.CurrentSlug.ShouldBe("secret-room");
        _store.State.AccentColor.ShouldBe("#6366f1");
    }

    [Fact]
    public void InvalidSpace_ResetsToDefault()
    {
        _dispatcher.Dispatch(new SpaceValidated("secret-room", "#6366f1"));
        _dispatcher.Dispatch(new InvalidSpace());

        _store.State.CurrentSlug.ShouldBe("default");
        _store.State.AccentColor.ShouldBe("#e94560");
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "SpaceStoreTests" -v minimal`
Expected: FAIL — files don't exist

**Step 3: Implement `SpaceStore`**

3a. `WebChat.Client/State/Space/SpaceState.cs`:
```csharp
namespace WebChat.Client.State.Space;

public sealed record SpaceState
{
    public string CurrentSlug { get; init; } = "default";
    public string AccentColor { get; init; } = "#e94560";

    public static SpaceState Initial => new();
}
```

3b. `WebChat.Client/State/Space/SpaceActions.cs`:
```csharp
namespace WebChat.Client.State.Space;

public record SelectSpace(string Slug) : IAction;
public record SpaceValidated(string Slug, string AccentColor) : IAction;
public record InvalidSpace : IAction;
```

3c. `WebChat.Client/State/Space/SpaceReducers.cs`:
```csharp
namespace WebChat.Client.State.Space;

public static class SpaceReducers
{
    public static SpaceState Reduce(SpaceState state, IAction action) => action switch
    {
        SpaceValidated a => state with { CurrentSlug = a.Slug, AccentColor = a.AccentColor },
        InvalidSpace => SpaceState.Initial,
        _ => state
    };
}
```

3d. `WebChat.Client/State/Space/SpaceStore.cs`:
```csharp
namespace WebChat.Client.State.Space;

public sealed class SpaceStore : IDisposable
{
    private readonly Store<SpaceState> _store;

    public SpaceStore(Dispatcher dispatcher)
    {
        _store = new Store<SpaceState>(SpaceState.Initial);

        dispatcher.RegisterHandler<SpaceValidated>(action =>
            _store.Dispatch(action, SpaceReducers.Reduce));
        dispatcher.RegisterHandler<InvalidSpace>(action =>
            _store.Dispatch(action, SpaceReducers.Reduce));
    }

    public SpaceState State => _store.State;
    public IObservable<SpaceState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "SpaceStoreTests" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Space/ Tests/Unit/WebChat.Client/State/SpaceStoreTests.cs
git commit -m "feat(webchat): add SpaceStore with actions and reducers"
```

---

### Task 6: Client-Side Space Effect + Service

**Files:**
- Create: `WebChat.Client/State/Effects/SpaceEffect.cs`
- Modify: `WebChat.Client/Contracts/ITopicService.cs` (add `JoinSpaceAsync`)
- Modify: `WebChat.Client/Services/TopicService.cs` (implement `JoinSpaceAsync`)
- Modify: `WebChat.Client/Extensions/ServiceCollectionExtensions.cs` (register `SpaceStore` + `SpaceEffect`)
- Modify: `WebChat.Client/Program.cs` (activate `SpaceEffect`)

**Step 1: Add `JoinSpaceAsync` to the topic service interface and implementation**

In `WebChat.Client/Contracts/ITopicService.cs`, add:
```csharp
Task<string?> JoinSpaceAsync(string spaceSlug);
```

In `WebChat.Client/Services/TopicService.cs`, add:
```csharp
public async Task<string?> JoinSpaceAsync(string spaceSlug)
{
    var hubConnection = connectionService.HubConnection;
    if (hubConnection is null)
    {
        return null;
    }

    return await hubConnection.InvokeAsync<string?>("JoinSpace", spaceSlug);
}
```

**Step 2: Add `spaceSlug` parameter to `GetAllTopicsAsync`**

In `WebChat.Client/Contracts/ITopicService.cs`:
```csharp
Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string spaceSlug = "default");
```

In `WebChat.Client/Services/TopicService.cs`:
```csharp
public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string spaceSlug = "default")
{
    var hubConnection = connectionService.HubConnection;
    if (hubConnection is null)
    {
        return [];
    }

    return await hubConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics", agentId, spaceSlug);
}
```

**Step 3: Create `SpaceEffect`**

`WebChat.Client/State/Effects/SpaceEffect.cs`:
```csharp
using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class SpaceEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private readonly NavigationManager _navigationManager;
    private readonly SpaceStore _spaceStore;

    public SpaceEffect(
        Dispatcher dispatcher,
        ITopicService topicService,
        NavigationManager navigationManager,
        SpaceStore spaceStore)
    {
        _dispatcher = dispatcher;
        _topicService = topicService;
        _navigationManager = navigationManager;
        _spaceStore = spaceStore;

        dispatcher.RegisterHandler<SelectSpace>(HandleSelectSpace);
    }

    private void HandleSelectSpace(SelectSpace action)
    {
        _ = HandleSelectSpaceAsync(action.Slug);
    }

    private async Task HandleSelectSpaceAsync(string slug)
    {
        // Don't re-join if already in this space
        if (_spaceStore.State.CurrentSlug == slug)
        {
            return;
        }

        var accentColor = await _topicService.JoinSpaceAsync(slug);
        if (accentColor is null)
        {
            // Invalid space — redirect to default
            _dispatcher.Dispatch(new InvalidSpace());
            _navigationManager.NavigateTo("/", replace: true);
            return;
        }

        // Clear topics and messages for space transition
        _dispatcher.Dispatch(new TopicsLoaded([]));
        _dispatcher.Dispatch(new ClearAllMessages());
        _dispatcher.Dispatch(new SpaceValidated(slug, accentColor));
    }

    public void Dispose()
    {
        // No subscriptions to dispose
    }
}
```

**Note:** `ClearAllMessages` action is needed — add it to `WebChat.Client/State/Messages/MessagesActions.cs` if it doesn't exist. If it does exist, use it. If not, add a `ClearAllMessages` action and handle it in `MessagesReducers` to clear all messages.

**Step 4: Register in DI**

In `WebChat.Client/Extensions/ServiceCollectionExtensions.cs`, add `SpaceStore` to `AddWebChatStores()`:
```csharp
services.AddScoped<SpaceStore>();
```

Add `SpaceEffect` to `AddWebChatEffects()`:
```csharp
services.AddScoped<SpaceEffect>();
```

In `WebChat.Client/Program.cs`, activate:
```csharp
_ = app.Services.GetRequiredService<SpaceEffect>();
```

**Step 5: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS

**Step 6: Commit**

```bash
git add WebChat.Client/Contracts/ITopicService.cs WebChat.Client/Services/TopicService.cs \
  WebChat.Client/State/Effects/SpaceEffect.cs WebChat.Client/State/Space/ \
  WebChat.Client/Extensions/ServiceCollectionExtensions.cs WebChat.Client/Program.cs
git commit -m "feat(webchat): add SpaceEffect with JoinSpace hub call and navigation"
```

---

### Task 7: Wire Space Slug Through Initialization and Agent Selection Effects

**Files:**
- Modify: `WebChat.Client/State/Effects/InitializationEffect.cs`
- Modify: `WebChat.Client/State/Effects/AgentSelectionEffect.cs`
- Modify: `WebChat.Client/State/Effects/SendMessageEffect.cs`

**Step 1: Update `InitializationEffect` to use space slug**

The initialization currently calls `_topicService.GetAllTopicsAsync(agentToSelect.Id)`. It needs to pass the current space slug. Inject `SpaceStore` and read `State.CurrentSlug`:

Add `SpaceStore spaceStore` to constructor parameters. Update the `GetAllTopicsAsync` call:
```csharp
var spaceSlug = _spaceStore.State.CurrentSlug;
var serverTopics = await _topicService.GetAllTopicsAsync(agentToSelect.Id, spaceSlug);
```

**Step 2: Update `AgentSelectionEffect` to use space slug**

Inject `SpaceStore` and update `LoadTopicsForAgentAsync`:
```csharp
var spaceSlug = _spaceStore.State.CurrentSlug;
var serverTopics = await _topicService.GetAllTopicsAsync(agentId, spaceSlug);
```

**Step 3: Update `SendMessageEffect` to set `SpaceSlug` on new topics**

When creating a new topic in `HandleSendMessageAsync`, set the space slug:
```csharp
topic = new StoredTopic
{
    TopicId = topicId,
    ChatId = TopicIdGenerator.GetChatIdForTopic(topicId),
    ThreadId = TopicIdGenerator.GetThreadIdForTopic(topicId),
    AgentId = state.SelectedAgentId!,
    Name = topicName,
    CreatedAt = DateTime.UtcNow,
    SpaceSlug = _spaceStore.State.CurrentSlug
};
```

Inject `SpaceStore` into `SendMessageEffect`.

**Step 4: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS

**Step 5: Commit**

```bash
git add WebChat.Client/State/Effects/InitializationEffect.cs \
  WebChat.Client/State/Effects/AgentSelectionEffect.cs \
  WebChat.Client/State/Effects/SendMessageEffect.cs
git commit -m "feat(webchat): wire space slug through initialization and topic creation"
```

---

### Task 8: Blazor Routing — `/{slug?}` Route Parameter

**Files:**
- Modify: `WebChat.Client/Components/Chat/ChatContainer.razor`

**Step 1: Add route parameter and dispatch `SelectSpace`**

Update `ChatContainer.razor`:
```razor
@page "/"
@page "/{Slug}"
@inherits StoreSubscriberComponent
@inject IDispatcher Dispatcher

<ApprovalModal />

<div class="chat-layout">
    <TopicList />

    <div class="chat-container">
        <MessageList />

        <ConnectionStatus />

        <div class="input-area">
            <ChatInput />
        </div>
    </div>
</div>

@code {
    [Parameter]
    public string? Slug { get; set; }

    protected override void OnInitialized()
    {
        var slug = string.IsNullOrEmpty(Slug) ? "default" : Slug;
        Dispatcher.Dispatch(new SelectSpace(slug));
        Dispatcher.Dispatch(new Initialize());
    }

    protected override void OnParametersSet()
    {
        // Handle route changes (navigating between spaces)
        var slug = string.IsNullOrEmpty(Slug) ? "default" : Slug;
        Dispatcher.Dispatch(new SelectSpace(slug));
    }
}
```

Add `@using WebChat.Client.State.Space` to the top or to `_Imports.razor`.

**Step 2: Adjust `SpaceEffect` to handle initialization ordering**

The `SelectSpace` action fires before `Initialize`. The `SpaceEffect` calls `JoinSpaceAsync` which needs a hub connection. Handle this: if the hub isn't connected yet, skip the effect — `InitializationEffect` will join the space during its flow.

Update `SpaceEffect.HandleSelectSpaceAsync`:
```csharp
private async Task HandleSelectSpaceAsync(string slug)
{
    if (_spaceStore.State.CurrentSlug == slug)
    {
        return;
    }

    // If hub isn't connected yet, just store the slug for InitializationEffect to use
    var accentColor = await _topicService.JoinSpaceAsync(slug);
    if (accentColor is null)
    {
        _dispatcher.Dispatch(new InvalidSpace());
        _navigationManager.NavigateTo("/", replace: true);
        return;
    }

    _dispatcher.Dispatch(new TopicsLoaded([]));
    _dispatcher.Dispatch(new ClearAllMessages());
    _dispatcher.Dispatch(new SpaceValidated(slug, accentColor));
}
```

Actually, simpler: Update `InitializationEffect` to call `JoinSpace` as part of its flow. The `SpaceEffect` only handles space *changes* (after initial load). During init:

In `InitializationEffect.HandleInitializeAsync`, after connecting and subscribing, join the space:
```csharp
// Join space
var spaceSlug = _spaceStore.State.CurrentSlug;
var accentColor = await _topicService.JoinSpaceAsync(spaceSlug);
if (accentColor is not null)
{
    _dispatcher.Dispatch(new SpaceValidated(spaceSlug, accentColor));
}
else
{
    // Invalid space during init — this shouldn't happen for "default" but handle gracefully
    _dispatcher.Dispatch(new InvalidSpace());
}
```

And `SelectSpace` in `ChatContainer.razor` should be handled BEFORE `Initialize`. Make `SpaceStore` accept `SelectSpace` purely as a slug setter (no effect):

Update `SpaceReducers`:
```csharp
SelectSpace a => state with { CurrentSlug = a.Slug },
```

Register in `SpaceStore`:
```csharp
dispatcher.RegisterHandler<SelectSpace>(action =>
    _store.Dispatch(action, SpaceReducers.Reduce));
```

Then `SpaceEffect` only triggers on `SelectSpace` when the hub IS connected (for space switches after init). `InitializationEffect` reads `_spaceStore.State.CurrentSlug` to join the correct space.

**Step 3: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS

**Step 4: Commit**

```bash
git add WebChat.Client/Components/Chat/ChatContainer.razor \
  WebChat.Client/State/Space/SpaceReducers.cs \
  WebChat.Client/State/Space/SpaceStore.cs \
  WebChat.Client/State/Effects/SpaceEffect.cs \
  WebChat.Client/State/Effects/InitializationEffect.cs
git commit -m "feat(webchat): add /{slug?} route and space initialization flow"
```

---

### Task 9: Logo Accent Color in MainLayout

**Files:**
- Modify: `WebChat.Client/Layout/MainLayout.razor`
- Delete (optional): `WebChat.Client/wwwroot/cat-logo.svg` (no longer used as `<img>`)

**Step 1: Inline the SVG and bind accent color**

Replace the `<img>` tag with inline SVG bound to `SpaceStore`:

```razor
@inherits LayoutComponentBase
@using WebChat.Client.Contracts
@using WebChat.Client.State.Space
@implements IDisposable
@inject IChatConnectionService ConnectionService
@inject IJSRuntime Js
@inject SpaceStore SpaceStore

<div class="main-layout">
    <header class="header">
        <div class="header-title">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 50" width="120" height="50" class="header-logo">
                <text x="60" y="38" text-anchor="middle" font-family="Arial, sans-serif" font-size="40"
                      fill="@_accentColor">ᓚᘏᗢ</text>
            </svg>
        </div>
        <!-- ... rest unchanged ... -->
    </header>
    <!-- ... rest unchanged ... -->
</div>
```

Add subscription in `@code` block:
```csharp
private string _accentColor = "#e94560";
private IDisposable? _spaceSubscription;

protected override Task OnInitializedAsync()
{
    ConnectionService.OnStateChanged += StateHasChanged;
    _spaceSubscription = SpaceStore.StateObservable.Subscribe(state =>
    {
        if (_accentColor != state.AccentColor)
        {
            _accentColor = state.AccentColor;
            InvokeAsync(StateHasChanged);
        }
    });
    return Task.CompletedTask;
}

public void Dispose()
{
    ConnectionService.OnStateChanged -= StateHasChanged;
    _spaceSubscription?.Dispose();
    _selfRef?.Dispose();
}
```

**Step 2: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS

**Step 3: Commit**

```bash
git add WebChat.Client/Layout/MainLayout.razor
git commit -m "feat(webchat): inline cat logo SVG with space-aware accent color"
```

---

### Task 10: WebChat Server Config — Add Spaces to `appsettings.json`

**Files:**
- Modify: `WebChat/appsettings.json` (add `Spaces` section — but note: the WebChat project is just the WASM host, spaces config lives in the Agent project's appsettings since the hub runs there)

This is already handled in Task 3 (Agent/appsettings.json). Verify that the test fixture in Task 3 also has the config.

**No separate work needed — this is a config-only step already covered.**

---

### Task 11: Final Integration — End-to-End Verification

**Files:**
- Modify: `Tests/Integration/WebChat/ChatHubIntegrationTests.cs` (add end-to-end space test)

**Step 1: Write integration test for full space workflow**

```csharp
[Fact]
public async Task FullSpaceWorkflow_CreateTopicsInDifferentSpaces_IsolatedCorrectly()
{
    // Arrange - join default space
    var accentColor = await _connection.InvokeAsync<string?>("JoinSpace", "default");
    accentColor.ShouldNotBeNull();

    // Create topic in default space
    var defaultTopic = new TopicMetadata(
        "e2e-default", 500L, 500L, "test-agent", "Default E2E",
        DateTimeOffset.UtcNow, null, null, "default");
    await _connection.InvokeAsync("SaveTopic", defaultTopic, true);

    // Switch to secret space
    var secretColor = await _connection.InvokeAsync<string?>("JoinSpace", "secret-room");
    secretColor.ShouldNotBeNull();
    secretColor.ShouldNotBe(accentColor);

    // Create topic in secret space
    var secretTopic = new TopicMetadata(
        "e2e-secret", 600L, 600L, "test-agent", "Secret E2E",
        DateTimeOffset.UtcNow, null, null, "secret-room");
    await _connection.InvokeAsync("SaveTopic", secretTopic, true);

    // Act - query both spaces
    var defaultTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
        "GetAllTopics", "test-agent", "default");
    var secretTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
        "GetAllTopics", "test-agent", "secret-room");

    // Assert - topics are isolated
    defaultTopics.ShouldContain(t => t.TopicId == "e2e-default");
    defaultTopics.ShouldNotContain(t => t.TopicId == "e2e-secret");
    secretTopics.ShouldContain(t => t.TopicId == "e2e-secret");
    secretTopics.ShouldNotContain(t => t.TopicId == "e2e-default");

    // Assert - invalid space returns empty
    var invalidTopics = await _connection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
        "GetAllTopics", "test-agent", "nonexistent");
    invalidTopics.ShouldBeEmpty();

    // Assert - JoinSpace with invalid slug returns null
    var invalidColor = await _connection.InvokeAsync<string?>("JoinSpace", "nonexistent");
    invalidColor.ShouldBeNull();
}
```

**Step 2: Run test**

Run: `dotnet test Tests/ --filter "FullSpaceWorkflow" -v minimal`
Expected: PASS

**Step 3: Run the full test suite one final time**

Run: `dotnet test Tests/ -v minimal`
Expected: All tests PASS

**Step 4: Commit**

```bash
git add Tests/Integration/WebChat/ChatHubIntegrationTests.cs
git commit -m "test: add end-to-end space workflow integration test"
```

---

### Task 12: Handle `ClearAllMessages` Action (if not already existing)

**Files:**
- Check: `WebChat.Client/State/Messages/MessagesActions.cs`
- Check: `WebChat.Client/State/Messages/MessagesReducers.cs`
- Check: `WebChat.Client/State/Messages/MessagesStore.cs`

**Step 1: Check if `ClearAllMessages` exists**

Search for `ClearAllMessages` in the codebase. If it doesn't exist:

Add to `MessagesActions.cs`:
```csharp
public record ClearAllMessages : IAction;
```

Add to `MessagesReducers.cs`:
```csharp
ClearAllMessages => state with { MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>() },
```

Register in `MessagesStore.cs`:
```csharp
dispatcher.RegisterHandler<ClearAllMessages>(action =>
    _store.Dispatch(action, MessagesReducers.Reduce));
```

**Step 2: Write test for it**

```csharp
[Fact]
public void ClearAllMessages_RemovesAllTopicMessages()
{
    // Arrange
    _dispatcher.Dispatch(new MessagesLoaded("topic-1", [new ChatMessageModel { Content = "msg1" }]));
    _dispatcher.Dispatch(new MessagesLoaded("topic-2", [new ChatMessageModel { Content = "msg2" }]));

    // Act
    _dispatcher.Dispatch(new ClearAllMessages());

    // Assert
    _store.State.MessagesByTopic.ShouldBeEmpty();
}
```

**Step 3: Run tests**

Run: `dotnet test Tests/ --filter "ClearAllMessages" -v minimal`
Expected: PASS

**Step 4: Commit**

```bash
git add WebChat.Client/State/Messages/
git commit -m "feat(webchat): add ClearAllMessages action for space transitions"
```

---

## Task Dependency Order

```
Task 1 (TopicMetadata SpaceSlug)
  └─ Task 2 (StoredTopic SpaceSlug)
       └─ Task 3 (Hub space validation + Redis filtering)
            ├─ Task 4 (Notification scoping)
            ├─ Task 5 (SpaceStore)
            │    └─ Task 6 (SpaceEffect + Service)
            │         └─ Task 7 (Wire effects)
            │              └─ Task 8 (Blazor routing)
            │                   └─ Task 9 (Logo accent color)
            └─ Task 11 (E2E integration test) — after Tasks 3-4
Task 12 (ClearAllMessages) — independent, needed by Task 6
```

Recommended execution order: 1 → 2 → 12 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 11
