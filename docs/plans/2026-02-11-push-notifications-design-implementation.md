# Push Notifications Implementation Plan

> **For Claude:** Execute this plan using subagents. Dispatch a fresh subagent per task
> using the Task tool (subagent_type: "general-purpose"). Each task is self-contained.
> NEVER skip test or review tasks. They are tracked separately and all must complete.

**Goal:** Send Web Push notifications to Android devices when the agent completes a response, so users receive alerts even when the app is not in the foreground.

**Architecture:** W3C Push API with VAPID keys. Backend sends push via `WebPush` NuGet library when a stream completes. Subscriptions stored in Redis. Service worker on client suppresses notifications when the app is already visible. Blazor WASM client manages subscription lifecycle via JS interop.

**Tech Stack:** .NET 10, StackExchange.Redis, WebPush NuGet, Blazor WASM, SignalR, Service Workers, Push API

**Design Document:** `docs/plans/2026-02-11-push-notifications-design.md`

---

## Task 0: Scaffolding

**Type:** SCAFFOLDING
**Depends on:** None

Set up the infrastructure needed by all triplets: NuGet package, domain types, interfaces, and VAPID configuration.

**Steps:**

1. Add `WebPush` NuGet package (v1.0.12) to `Infrastructure/Infrastructure.csproj`:
   ```xml
   <PackageReference Include="WebPush" Version="1.0.12" />
   ```

2. Create `Domain/DTOs/WebChat/PushSubscriptionDto.cs`:
   ```csharp
   namespace Domain.DTOs.WebChat;

   public record PushSubscriptionDto(string Endpoint, string P256dh, string Auth);
   ```

3. Create `Domain/Contracts/IPushSubscriptionStore.cs`:
   ```csharp
   using Domain.DTOs.WebChat;

   namespace Domain.Contracts;

   public interface IPushSubscriptionStore
   {
       Task SaveAsync(string userId, PushSubscriptionDto subscription, CancellationToken ct = default);
       Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default);
       Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default);
       Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(CancellationToken ct = default);
   }
   ```

4. Create `Domain/Contracts/IPushNotificationService.cs`:
   ```csharp
   namespace Domain.Contracts;

   public interface IPushNotificationService
   {
       Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default);
   }
   ```

5. Add VAPID configuration to `Agent/Settings/AgentSettings.cs`. Add a new record:
   ```csharp
   public record WebPushConfiguration
   {
       public string? PublicKey { get; init; }
       public string? PrivateKey { get; init; }
       public string? Subject { get; init; }
   }
   ```
   And add `public WebPushConfiguration? WebPush { get; init; }` to `AgentSettings`.

**Verification:** `dotnet build` succeeds for the solution.

**Commit:** `git commit -m "chore: add push notification scaffolding (DTOs, interfaces, NuGet)"`

---

## Triplet 1: RedisPushSubscriptionStore

### Task 1.1: Write failing tests for RedisPushSubscriptionStore

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 0

**Design requirements being tested:**
- Save a push subscription for a user (userId + PushSubscriptionDto)
- Remove a specific subscription by userId and endpoint
- Remove a subscription by endpoint only (for expired subscription cleanup)
- Get all subscriptions across all users
- Multiple subscriptions per user (multiple devices)
- Subscriptions stored as Redis hash: key `push:subs:{userId}`, field = endpoint, value = JSON `{ "p256dh": "...", "auth": "..." }`

**Files:**
- Create: `Tests/Integration/StateManagers/RedisPushSubscriptionStoreTests.cs`

**What to test:**

```csharp
using Domain.DTOs.WebChat;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public sealed class RedisPushSubscriptionStoreTests(RedisFixture fixture)
    : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisPushSubscriptionStore _store = new(fixture.Connection);
    private readonly List<string> _createdUserIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var db = fixture.Connection.GetDatabase();
        foreach (var userId in _createdUserIds)
        {
            await db.KeyDeleteAsync($"push:subs:{userId}");
        }
    }

    private PushSubscriptionDto CreateSubscription(string endpoint = "https://fcm.googleapis.com/fcm/send/abc123") =>
        new(endpoint, "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUb...", "tBHItJI5svbpC7sc9d8M2w==");

    [Fact]
    public async Task SaveAsync_NewSubscription_StoresInRedis()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = CreateSubscription();

        await _store.SaveAsync(userId, sub);

        var all = await _store.GetAllAsync();
        all.ShouldContain(x => x.UserId == userId && x.Subscription.Endpoint == sub.Endpoint);
    }

    [Fact]
    public async Task SaveAsync_MultipleDevices_StoresAll()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = CreateSubscription("https://fcm.googleapis.com/fcm/send/device1");
        var sub2 = CreateSubscription("https://fcm.googleapis.com/fcm/send/device2");

        await _store.SaveAsync(userId, sub1);
        await _store.SaveAsync(userId, sub2);

        var all = await _store.GetAllAsync();
        var userSubs = all.Where(x => x.UserId == userId).ToList();
        userSubs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SaveAsync_SameEndpoint_OverwritesPrevious()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/same", "key1", "auth1");
        var sub2 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/same", "key2", "auth2");

        await _store.SaveAsync(userId, sub1);
        await _store.SaveAsync(userId, sub2);

        var all = await _store.GetAllAsync();
        var userSubs = all.Where(x => x.UserId == userId).ToList();
        userSubs.Count.ShouldBe(1);
        userSubs[0].Subscription.P256dh.ShouldBe("key2");
    }

    [Fact]
    public async Task RemoveAsync_ExistingSubscription_RemovesIt()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = CreateSubscription();

        await _store.SaveAsync(userId, sub);
        await _store.RemoveAsync(userId, sub.Endpoint);

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.UserId == userId);
    }

    [Fact]
    public async Task RemoveAsync_NonExistentSubscription_DoesNotThrow()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);

        await Should.NotThrowAsync(() => _store.RemoveAsync(userId, "https://nonexistent.example.com"));
    }

    [Fact]
    public async Task RemoveByEndpointAsync_RemovesFromCorrectUser()
    {
        var userId1 = $"test-user-{Guid.NewGuid():N}";
        var userId2 = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId1);
        _createdUserIds.Add(userId2);
        var sharedEndpoint = "https://fcm.googleapis.com/fcm/send/shared";

        await _store.SaveAsync(userId1, CreateSubscription(sharedEndpoint));
        await _store.SaveAsync(userId2, CreateSubscription("https://fcm.googleapis.com/fcm/send/other"));

        await _store.RemoveByEndpointAsync(sharedEndpoint);

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == sharedEndpoint);
        all.ShouldContain(x => x.UserId == userId2);
    }

    [Fact]
    public async Task GetAllAsync_EmptyStore_ReturnsEmptyList()
    {
        // Use a unique prefix to avoid interference from other tests
        // The store returns ALL subscriptions, so this test relies on cleanup
        var all = await _store.GetAllAsync();
        all.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetAllAsync_MultipleUsers_ReturnsAll()
    {
        var userId1 = $"test-user-{Guid.NewGuid():N}";
        var userId2 = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId1);
        _createdUserIds.Add(userId2);

        await _store.SaveAsync(userId1, CreateSubscription("https://fcm.googleapis.com/fcm/send/u1d1"));
        await _store.SaveAsync(userId2, CreateSubscription("https://fcm.googleapis.com/fcm/send/u2d1"));

        var all = await _store.GetAllAsync();
        all.ShouldContain(x => x.UserId == userId1);
        all.ShouldContain(x => x.UserId == userId2);
    }
}
```

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~RedisPushSubscriptionStoreTests"`
Expected: ALL tests FAIL (class `RedisPushSubscriptionStore` does not exist)

**Commit:** `git commit -m "test: add failing tests for RedisPushSubscriptionStore"`

---

### Task 1.2: Implement RedisPushSubscriptionStore

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.1 must be complete (failing tests must exist)

**Goal:** Write the minimal code to make ALL tests from Task 1.1 pass.

**Files:**
- Create: `Infrastructure/StateManagers/RedisPushSubscriptionStore.cs`
- Reference: `Tests/Integration/StateManagers/RedisPushSubscriptionStoreTests.cs` (already exists)

**Implementation:**

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisPushSubscriptionStore(IConnectionMultiplexer redis) : IPushSubscriptionStore
{
    private const string KeyPrefix = "push:subs:";

    public async Task SaveAsync(string userId, PushSubscriptionDto subscription, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var value = JsonSerializer.Serialize(new { subscription.P256dh, subscription.Auth });
        await db.HashSetAsync($"{KeyPrefix}{userId}", subscription.Endpoint, value);
    }

    public async Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        await db.HashDeleteAsync($"{KeyPrefix}{userId}", endpoint);
    }

    public async Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        var server = redis.GetServers()[0];
        var db = redis.GetDatabase();

        await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
        {
            await db.HashDeleteAsync(key, endpoint);
        }
    }

    public async Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(
        CancellationToken ct = default)
    {
        var server = redis.GetServers()[0];
        var db = redis.GetDatabase();
        var results = new List<(string, PushSubscriptionDto)>();

        await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*"))
        {
            var userId = key.ToString()[KeyPrefix.Length..];
            var entries = await db.HashGetAllAsync(key);

            foreach (var entry in entries)
            {
                var data = JsonSerializer.Deserialize<JsonElement>(entry.Value!);
                var sub = new PushSubscriptionDto(
                    entry.Name!,
                    data.GetProperty("P256dh").GetString()!,
                    data.GetProperty("Auth").GetString()!);
                results.Add((userId, sub));
            }
        }

        return results;
    }
}
```

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~RedisPushSubscriptionStoreTests"`
Expected: ALL tests PASS

**Commit:** `git commit -m "feat: implement RedisPushSubscriptionStore"`

---

### Task 1.3: Adversarial review of RedisPushSubscriptionStore

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.2 must be complete (implementation must exist and tests pass)

**Your role:** You are an adversarial reviewer. Your job is to BREAK this implementation, not approve it. Assume the implementation is wrong until you prove otherwise.

**Design requirements to verify:**
- Save a push subscription for a user (userId + PushSubscriptionDto with Endpoint, P256dh, Auth)
- Remove a specific subscription by userId and endpoint
- Remove a subscription by endpoint only (scans all users — for expired subscription cleanup)
- Get all subscriptions across all users, returning (UserId, PushSubscriptionDto) tuples
- Multiple subscriptions per user (multiple devices)
- Subscriptions stored as Redis hash: key `push:subs:{userId}`, field = endpoint, value = JSON with P256dh and Auth
- Same endpoint for same user overwrites (upsert behavior)

**Review checklist:**

1. **Design compliance** — Read `Infrastructure/StateManagers/RedisPushSubscriptionStore.cs` and compare against EACH requirement listed above. Check line by line. Is anything missing? Misinterpreted? Over-built?

2. **Test adequacy** — Read `Tests/Integration/StateManagers/RedisPushSubscriptionStoreTests.cs`. Do the tests actually test what they claim? Could the tests pass with a WRONG implementation?

3. **Edge cases** — Try to break it:
   - Endpoints with special characters (query strings, fragments)
   - Very long endpoint URLs
   - Concurrent save and remove operations
   - Empty strings for userId, endpoint, P256dh, Auth
   - What happens if Redis connection drops mid-operation?

4. **Error handling** — What happens with null inputs? Does it follow the project convention of using `ArgumentNullException.ThrowIfNull()`?

5. **Code style** — Check against `.claude/rules/dotnet-style.md`: file-scoped namespaces, primary constructors, LINQ preference, no unnecessary comments.

6. **Integration** — Does it work with `IConnectionMultiplexer` as injected in `InjectorModule.AddRedis()`? Is the key pattern compatible with other Redis stores?

**You MUST write and run additional tests.** Minimum: 3 additional tests. Suggested:
- Test with endpoint containing query parameters
- Test RemoveByEndpointAsync when endpoint doesn't exist anywhere
- Test SaveAsync preserves P256dh and Auth correctly after round-trip

**What to produce:**
- List of issues found (Critical / Important / Minor)
- Additional tests written and their results
- Verdict: PASS or FAIL

**If FAIL:** Create fix tasks. The implementer fixes issues, then this review runs again.

**Commit additional tests:** `git commit -m "test: add adversarial tests for RedisPushSubscriptionStore"`

---

## Triplet 2: WebPushNotificationService

### Task 2.1: Write failing tests for WebPushNotificationService

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.3 (RedisPushSubscriptionStore must be reviewed and passing)

**Design requirements being tested:**
- Uses `WebPush` NuGet library's `WebPushClient` to send push notifications
- Fetches all subscriptions from `IPushSubscriptionStore.GetAllAsync()`
- Sends push payload (JSON with title, body, url) to each subscription
- On HTTP 410 Gone response: removes expired subscription via `IPushSubscriptionStore.RemoveByEndpointAsync()`
- Catches and logs send failures without blocking the caller
- Configurable VAPID details (public key, private key, subject)
- Sends to ALL subscriptions (space filtering is not needed at this level — design says "fetches all subscriptions, sends push to each")

**Files:**
- Create: `Tests/Unit/Infrastructure/WebPushNotificationServiceTests.cs`

**What to test:**

```csharp
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using WebPush;

namespace Tests.Unit.Infrastructure;

public sealed class WebPushNotificationServiceTests
{
    private readonly Mock<IPushSubscriptionStore> _mockStore;
    private readonly Mock<WebPushClient> _mockWebPushClient;
    private readonly WebPushNotificationService _sut;

    public WebPushNotificationServiceTests()
    {
        _mockStore = new Mock<IPushSubscriptionStore>();
        _mockWebPushClient = new Mock<WebPushClient>();

        _sut = new WebPushNotificationService(
            _mockStore.Object,
            _mockWebPushClient.Object,
            new VapidDetails("mailto:test@example.com", "BPublicKey", "PrivateKey"),
            NullLogger<WebPushNotificationService>.Instance);
    }

    [Fact]
    public async Task SendToSpaceAsync_WithSubscriptions_SendsToAll()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1")),
            ("user2", new PushSubscriptionDto("https://endpoint2", "key2", "auth2"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockWebPushClient.Verify(c => c.SendNotificationAsync(
            It.IsAny<PushSubscription>(),
            It.IsAny<string>(),
            It.IsAny<VapidDetails>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SendToSpaceAsync_WithNoSubscriptions_DoesNotSend()
    {
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string, PushSubscriptionDto)>());

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockWebPushClient.Verify(c => c.SendNotificationAsync(
            It.IsAny<PushSubscription>(),
            It.IsAny<string>(),
            It.IsAny<VapidDetails>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendToSpaceAsync_On410Gone_RemovesExpiredSubscription()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://expired-endpoint", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebPushException("Gone", null!, null!, System.Net.HttpStatusCode.Gone));

        await _sut.SendToSpaceAsync("default", "Title", "Body", "/default");

        _mockStore.Verify(s => s.RemoveByEndpointAsync("https://expired-endpoint", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendToSpaceAsync_OnOtherError_DoesNotThrow()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://error-endpoint", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WebPushException("Server Error", null!, null!, System.Net.HttpStatusCode.InternalServerError));

        await Should.NotThrowAsync(() => _sut.SendToSpaceAsync("default", "Title", "Body", "/default"));
    }

    [Fact]
    public async Task SendToSpaceAsync_PayloadContainsTitleBodyUrl()
    {
        var subs = new List<(string UserId, PushSubscriptionDto Subscription)>
        {
            ("user1", new PushSubscriptionDto("https://endpoint1", "key1", "auth1"))
        };
        _mockStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(subs);
        string? capturedPayload = null;
        _mockWebPushClient
            .Setup(c => c.SendNotificationAsync(
                It.IsAny<PushSubscription>(),
                It.IsAny<string>(),
                It.IsAny<VapidDetails>(),
                It.IsAny<CancellationToken>()))
            .Callback<PushSubscription, string, VapidDetails, CancellationToken>((_, payload, _, _) => capturedPayload = payload)
            .Returns(Task.CompletedTask);

        await _sut.SendToSpaceAsync("myspace", "New Response", "Agent replied", "/myspace");

        capturedPayload.ShouldNotBeNull();
        capturedPayload.ShouldContain("New Response");
        capturedPayload.ShouldContain("Agent replied");
        capturedPayload.ShouldContain("/myspace");
    }
}
```

**Note:** The `WebPushClient.SendNotificationAsync` method must be virtual/overridable for Moq. If it's not, the implementation should accept an abstraction or wrapper. Check the `WebPush` library's `WebPushClient` class — if its methods aren't virtual, create a thin wrapper interface like `IWebPushClient` with `SendNotificationAsync`. Adjust tests accordingly.

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~WebPushNotificationServiceTests"`
Expected: ALL tests FAIL (class `WebPushNotificationService` does not exist)

**Commit:** `git commit -m "test: add failing tests for WebPushNotificationService"`

---

### Task 2.2: Implement WebPushNotificationService

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.1 must be complete (failing tests must exist)

**Goal:** Write the minimal code to make ALL tests from Task 2.1 pass.

**Files:**
- Create: `Infrastructure/Clients/Messaging/WebChat/WebPushNotificationService.cs`
- Possibly create: `Domain/Contracts/IWebPushClient.cs` (if `WebPushClient` methods aren't virtual)
- Reference: `Tests/Unit/Infrastructure/WebPushNotificationServiceTests.cs`

**Implementation approach:**

The service should:
1. Accept `IPushSubscriptionStore`, a web push client (or wrapper), `VapidDetails`, and `ILogger` via constructor
2. In `SendToSpaceAsync`: fetch all subscriptions, serialize payload as JSON `{title, body, url}`, send to each
3. Catch `WebPushException` with status 410 → call `RemoveByEndpointAsync`
4. Catch all other exceptions → log warning, continue to next subscription

If `WebPushClient.SendNotificationAsync` is not virtual, create `IWebPushClient` wrapper:
```csharp
public interface IWebPushClient
{
    Task SendNotificationAsync(PushSubscription subscription, string payload, VapidDetails vapidDetails, CancellationToken ct = default);
}
```

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~WebPushNotificationServiceTests"`
Expected: ALL tests PASS

**Commit:** `git commit -m "feat: implement WebPushNotificationService"`

---

### Task 2.3: Adversarial review of WebPushNotificationService

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.2 must be complete

**Your role:** Adversarial reviewer. Try to BREAK the implementation.

**Design requirements to verify:**
- Uses WebPush library to send push notifications to each subscription
- Fetches all subscriptions from store
- JSON payload contains title, body, url
- HTTP 410 Gone → removes expired subscription
- Other errors logged but do not throw/block
- VAPID details passed to every send call

**Review checklist:**

1. **Design compliance** — Does the implementation match all requirements?
2. **Test adequacy** — Could tests pass with a wrong implementation? Do mocks match real API?
3. **Edge cases** — What if store returns subscriptions with invalid endpoints? What if VapidDetails are null?
4. **Error handling** — Does it handle all `WebPushException` status codes appropriately? What about non-WebPush exceptions (network errors)?
5. **Concurrency** — Could sending to many subscriptions be parallelized? Is sequential OK for now?
6. **Code style** — File-scoped namespace, primary constructor, LINQ preference, no XML docs

**You MUST write and run additional tests.** Minimum: 3. Suggested:
- Test that a failure sending to one subscription doesn't prevent sending to others
- Test that `VapidDetails` are passed correctly to each send call
- Test behavior when store's `GetAllAsync` throws

**What to produce:**
- Issues list (Critical / Important / Minor)
- Additional tests and results
- Verdict: PASS or FAIL

**Commit additional tests:** `git commit -m "test: add adversarial tests for WebPushNotificationService"`

---

## Triplet 3: HubNotifier Push Integration

### Task 3.1: Write failing tests for HubNotifier push integration

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 2.3 (WebPushNotificationService must be reviewed and passing)

**Design requirements being tested:**
- When `NotifyStreamChangedAsync` is called with `StreamChangeType.Completed`, it ALSO calls `IPushNotificationService.SendToSpaceAsync()` with the space slug, title "New response", a body, and the space URL
- When `NotifyStreamChangedAsync` is called with `StreamChangeType.Started` or `StreamChangeType.Cancelled`, it does NOT call push notification service
- The existing SignalR notification behavior (via `IHubNotificationSender`) is unchanged
- Push notification failures do not block the SignalR notification

**Files:**
- Modify: `Tests/Unit/Infrastructure/HubNotifierTests.cs` (add new tests)

**Context:** The existing `HubNotifierTests.cs` mocks `IHubNotificationSender`. The new tests must also mock `IPushNotificationService`. The `HubNotifier` constructor will need to accept `IPushNotificationService` as an additional dependency.

**What to test:**

Add these tests to the existing file:

```csharp
[Fact]
public async Task NotifyStreamChangedAsync_Completed_SendsPushNotification()
{
    var mockSender = new Mock<IHubNotificationSender>();
    var mockPush = new Mock<IPushNotificationService>();
    var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
    var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1", "myspace");

    await notifier.NotifyStreamChangedAsync(notification);

    mockPush.Verify(p => p.SendToSpaceAsync(
        "myspace",
        It.IsAny<string>(),
        It.IsAny<string>(),
        It.IsAny<string>(),
        It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task NotifyStreamChangedAsync_Started_DoesNotSendPush()
{
    var mockSender = new Mock<IHubNotificationSender>();
    var mockPush = new Mock<IPushNotificationService>();
    var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
    var notification = new StreamChangedNotification(StreamChangeType.Started, "topic-1", "myspace");

    await notifier.NotifyStreamChangedAsync(notification);

    mockPush.Verify(p => p.SendToSpaceAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task NotifyStreamChangedAsync_Cancelled_DoesNotSendPush()
{
    var mockSender = new Mock<IHubNotificationSender>();
    var mockPush = new Mock<IPushNotificationService>();
    var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
    var notification = new StreamChangedNotification(StreamChangeType.Cancelled, "topic-1", "myspace");

    await notifier.NotifyStreamChangedAsync(notification);

    mockPush.Verify(p => p.SendToSpaceAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
        It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task NotifyStreamChangedAsync_Completed_StillSendsSignalR()
{
    var mockSender = new Mock<IHubNotificationSender>();
    var mockPush = new Mock<IPushNotificationService>();
    var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
    var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1", "myspace");

    await notifier.NotifyStreamChangedAsync(notification);

    mockSender.Verify(s => s.SendToGroupAsync(
        "space:myspace", "OnStreamChanged",
        It.IsAny<StreamChangedNotification>(),
        It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task NotifyStreamChangedAsync_PushThrows_DoesNotBlockSignalR()
{
    var mockSender = new Mock<IHubNotificationSender>();
    var mockPush = new Mock<IPushNotificationService>();
    mockPush.Setup(p => p.SendToSpaceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new Exception("Push failed"));
    var notifier = new HubNotifier(mockSender.Object, mockPush.Object);
    var notification = new StreamChangedNotification(StreamChangeType.Completed, "topic-1", "myspace");

    await Should.NotThrowAsync(() => notifier.NotifyStreamChangedAsync(notification));

    mockSender.Verify(s => s.SendToGroupAsync(
        "space:myspace", "OnStreamChanged",
        It.IsAny<StreamChangedNotification>(),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

**Important:** The existing tests instantiate `HubNotifier(mockSender.Object)` with a single argument. After adding the push dependency, they must be updated to `HubNotifier(mockSender.Object, mockPush.Object)` (or use a null/no-op push service). Update existing tests to pass a `Mock<IPushNotificationService>().Object` as the second argument so they continue to work.

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~HubNotifierTests"`
Expected: New tests FAIL (HubNotifier doesn't accept IPushNotificationService yet). Existing tests may also fail if constructor changed.

**Commit:** `git commit -m "test: add failing tests for HubNotifier push integration"`

---

### Task 3.2: Implement HubNotifier push integration

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 3.1 must be complete

**Goal:** Modify `HubNotifier` to accept `IPushNotificationService` and send push on stream completed.

**Files:**
- Modify: `Infrastructure/Clients/Messaging/WebChat/HubNotifier.cs`
- Reference: `Tests/Unit/Infrastructure/HubNotifierTests.cs`

**Implementation:**

Change the primary constructor to accept both dependencies:
```csharp
public sealed class HubNotifier(IHubNotificationSender sender, IPushNotificationService pushService) : INotifier
```

In `NotifyStreamChangedAsync`, after the existing SignalR notification, add:
```csharp
if (notification.ChangeType == StreamChangeType.Completed)
{
    var url = notification.SpaceSlug is not null ? $"/{notification.SpaceSlug}" : "/";
    try
    {
        await pushService.SendToSpaceAsync(
            notification.SpaceSlug ?? "default",
            "New response",
            "The agent has finished responding",
            url,
            cancellationToken);
    }
    catch
    {
        // Push notification failures must not block the SignalR notification
    }
}
```

Send the SignalR notification FIRST, then attempt push. The try/catch ensures push failures don't block.

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~HubNotifierTests"`
Expected: ALL tests PASS (both old and new)

**Commit:** `git commit -m "feat: trigger push notification on stream completed in HubNotifier"`

---

### Task 3.3: Adversarial review of HubNotifier push integration

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 3.2 must be complete

**Your role:** Adversarial reviewer.

**Design requirements to verify:**
- Push notification sent ONLY on `StreamChangeType.Completed`
- NOT sent on `Started` or `Cancelled`
- Existing SignalR notification behavior unchanged for ALL notification types
- Push failures do not block or affect SignalR notifications
- URL constructed correctly from space slug (e.g., `"/myspace"` for slug `"myspace"`, `"/"` for null)
- Push sends to the correct space slug

**Review checklist:**
1. **Design compliance** — Does the modified HubNotifier match all requirements?
2. **Regression check** — Do ALL existing tests still pass? Are existing notification types unaffected?
3. **Edge cases** — What if space slug is null? Empty? What about the other notification methods (TopicChanged, etc.) — are they untouched?
4. **Error handling** — Does the catch block catch ALL exception types? Does it log?
5. **Ordering** — Is SignalR sent before push? (Important: push must not delay SignalR)

**You MUST write and run additional tests.** Minimum: 3. Suggested:
- Test `NotifyStreamChangedAsync` with null space slug
- Verify other notification methods (`NotifyTopicChangedAsync`, etc.) do NOT trigger push
- Test that SignalR is sent even when push throws synchronously

**What to produce:**
- Issues list, additional tests, verdict

**Commit additional tests:** `git commit -m "test: add adversarial tests for HubNotifier push integration"`

---

## Triplet 4: ChatHub Push Subscription Methods & DI Registration

### Task 4.1: Write failing tests for ChatHub push subscription methods

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 1.3 (RedisPushSubscriptionStore must be working)

**Design requirements being tested:**
- ChatHub has `SubscribePush(PushSubscriptionDto subscription)` method that stores the subscription for the registered user
- ChatHub has `UnsubscribePush(string endpoint)` method that removes the subscription for the registered user
- `SubscribePush` requires the user to be registered (throws `HubException` otherwise)
- `UnsubscribePush` requires the user to be registered (throws `HubException` otherwise)

**Files:**
- Create: `Tests/Integration/WebChat/ChatHubPushSubscriptionTests.cs`

**What to test:**

Use the existing `WebChatServerFixture` pattern. The fixture will need updating to register `IPushSubscriptionStore` and `IPushNotificationService`. For these tests, a mock or in-memory implementation of `IPushSubscriptionStore` can be used, but since the fixture already has Redis, use the real `RedisPushSubscriptionStore`.

```csharp
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class ChatHubPushSubscriptionTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", "push-test-user");
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SubscribePush_WithRegisteredUser_Succeeds()
    {
        var subscription = new PushSubscriptionDto(
            "https://fcm.googleapis.com/fcm/send/test123",
            "BNcRdreALRFXTkOOUHK1EtK...",
            "tBHItJI5svbpC7sc...");

        await Should.NotThrowAsync(() =>
            _connection.InvokeAsync("SubscribePush", subscription));
    }

    [Fact]
    public async Task UnsubscribePush_WithRegisteredUser_Succeeds()
    {
        await Should.NotThrowAsync(() =>
            _connection.InvokeAsync("UnsubscribePush", "https://fcm.googleapis.com/fcm/send/test123"));
    }

    [Fact]
    public async Task SubscribePush_WithoutRegisteredUser_ThrowsHubException()
    {
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        var subscription = new PushSubscriptionDto("https://endpoint", "key", "auth");

        await Should.ThrowAsync<HubException>(() =>
            unregisteredConnection.InvokeAsync("SubscribePush", subscription));

        await unregisteredConnection.DisposeAsync();
    }

    [Fact]
    public async Task UnsubscribePush_WithoutRegisteredUser_ThrowsHubException()
    {
        var unregisteredConnection = fixture.CreateHubConnection();
        await unregisteredConnection.StartAsync();

        await Should.ThrowAsync<HubException>(() =>
            unregisteredConnection.InvokeAsync("UnsubscribePush", "https://endpoint"));

        await unregisteredConnection.DisposeAsync();
    }
}
```

**Note:** The `WebChatServerFixture` must be updated to register `IPushSubscriptionStore` (using `RedisPushSubscriptionStore`) and `IPushNotificationService` (using a mock or no-op implementation). This update should be part of this task.

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChatHubPushSubscriptionTests"`
Expected: ALL tests FAIL (ChatHub doesn't have `SubscribePush`/`UnsubscribePush` methods yet)

**Commit:** `git commit -m "test: add failing tests for ChatHub push subscription methods"`

---

### Task 4.2: Implement ChatHub push subscription methods & DI registration

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 4.1 must be complete

**Goal:** Add push subscription methods to ChatHub and register push services in DI.

**Files:**
- Modify: `Agent/Hubs/ChatHub.cs` — add `SubscribePush` and `UnsubscribePush` methods
- Modify: `Agent/Modules/InjectorModule.cs` — register `IPushSubscriptionStore` and `IPushNotificationService` in `AddWebClient` and `AddRedis`
- Modify: `Tests/Integration/Fixtures/WebChatServerFixture.cs` — register push services for tests

**Implementation for ChatHub:**

Add `IPushSubscriptionStore` to the primary constructor:
```csharp
public sealed class ChatHub(
    IAgentFactory agentFactory,
    IOptionsMonitor<AgentRegistryOptions> registryOptions,
    IThreadStateStore threadStateStore,
    WebChatMessengerClient messengerClient,
    ChatThreadResolver threadResolver,
    INotifier hubNotifier,
    IPushSubscriptionStore pushSubscriptionStore) : Hub
```

Add methods:
```csharp
public async Task SubscribePush(PushSubscriptionDto subscription)
{
    var userId = GetRegisteredUserId()
        ?? throw new HubException("User not registered. Call RegisterUser first.");
    await pushSubscriptionStore.SaveAsync(userId, subscription);
}

public async Task UnsubscribePush(string endpoint)
{
    var userId = GetRegisteredUserId()
        ?? throw new HubException("User not registered. Call RegisterUser first.");
    await pushSubscriptionStore.RemoveAsync(userId, endpoint);
}
```

**Implementation for InjectorModule:**

In `AddRedis`, also register the push subscription store:
```csharp
.AddSingleton<IPushSubscriptionStore>(sp => new RedisPushSubscriptionStore(
    sp.GetRequiredService<IConnectionMultiplexer>()))
```

In `AddWebClient`, register the push notification service:
```csharp
.AddSingleton<IPushNotificationService>(sp =>
{
    var config = settings.WebPush;
    if (config?.PublicKey is null || config.PrivateKey is null || config.Subject is null)
    {
        return new NullPushNotificationService(); // No-op when VAPID not configured
    }
    var vapidDetails = new VapidDetails(config.Subject, config.PublicKey, config.PrivateKey);
    return new WebPushNotificationService(
        sp.GetRequiredService<IPushSubscriptionStore>(),
        new WebPushClient(),
        vapidDetails,
        sp.GetRequiredService<ILogger<WebPushNotificationService>>());
})
```

Create a `NullPushNotificationService` (no-op implementation for when VAPID keys aren't configured):
```csharp
public sealed class NullPushNotificationService : IPushNotificationService
{
    public Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default)
        => Task.CompletedTask;
}
```

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChatHubPushSubscriptionTests"`
Expected: ALL tests PASS

Also run: `dotnet test Tests/ --filter "FullyQualifiedName~ChatHubIntegrationTests"`
Expected: ALL existing tests PASS (no regression)

**Commit:** `git commit -m "feat: add push subscription methods to ChatHub and register DI"`

---

### Task 4.3: Adversarial review of ChatHub push subscription & DI

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 4.2 must be complete

**Your role:** Adversarial reviewer.

**Design requirements to verify:**
- `SubscribePush` stores subscription for the calling user
- `UnsubscribePush` removes subscription for the calling user
- Both methods require user registration (throw HubException otherwise)
- DI registers `IPushSubscriptionStore` and `IPushNotificationService`
- When VAPID keys are not configured, a no-op `NullPushNotificationService` is used
- HubNotifier now receives `IPushNotificationService` via DI
- Existing ChatHub integration tests still pass

**Review checklist:**
1. **Design compliance** — All requirements met?
2. **DI correctness** — Lifetimes correct (singleton)? Dependencies resolved correctly?
3. **Regression** — Run ALL existing ChatHub integration tests
4. **Edge cases** — What if subscription DTO has null fields? Empty endpoint?
5. **Security** — Can a user subscribe with another user's ID? (Should only use registered user ID)

**You MUST write and run additional tests.** Minimum: 3. Suggested:
- Subscribe, then unsubscribe, verify the subscription is removed (round-trip)
- Subscribe from two different connections with same user ID
- Verify that NullPushNotificationService is a harmless no-op

**What to produce:**
- Issues list, additional tests, verdict

**Commit additional tests:** `git commit -m "test: add adversarial tests for ChatHub push subscription"`

---

## Triplet 5: VAPID Config & AppConfig Exposure

### Task 5.1: Write failing tests for VAPID config exposure

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 0

**Design requirements being tested:**
- The VAPID public key is exposed through `GET /api/config` response
- The `AppConfig` record includes a `VapidPublicKey` field (nullable string)
- When VAPID keys are configured, the public key is returned
- When VAPID keys are NOT configured, `VapidPublicKey` is null

**Files:**
- Create: `Tests/Unit/WebChat/VapidConfigTests.cs`

**What to test:**

```csharp
using Shouldly;
using System.Net.Http.Json;

namespace Tests.Unit.WebChat;

public sealed class VapidConfigTests
{
    [Fact]
    public void AppConfig_WithVapidPublicKey_IncludesIt()
    {
        // Test the WebChat.Client AppConfig record includes VapidPublicKey
        var config = new WebChat.Client.Services.AppConfig("http://localhost:5000", [], "BTestPublicKey123");

        config.VapidPublicKey.ShouldBe("BTestPublicKey123");
    }

    [Fact]
    public void AppConfig_WithoutVapidPublicKey_IsNull()
    {
        var config = new WebChat.Client.Services.AppConfig("http://localhost:5000", [], null);

        config.VapidPublicKey.ShouldBeNull();
    }
}
```

**Note:** The `AppConfig` record needs to be updated on BOTH the WebChat server side (`WebChat/Program.cs`) and the WebChat client side (`WebChat.Client/Services/ConfigService.cs`). The test verifies the client-side record shape.

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~VapidConfigTests"`
Expected: ALL tests FAIL (AppConfig doesn't have VapidPublicKey field yet)

**Commit:** `git commit -m "test: add failing tests for VAPID config exposure"`

---

### Task 5.2: Implement VAPID config exposure

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 5.1 must be complete

**Goal:** Add `VapidPublicKey` to AppConfig on both server and client, and expose it via `/api/config`.

**Files:**
- Modify: `WebChat/Program.cs` — update `AppConfig` record and `/api/config` endpoint
- Modify: `WebChat.Client/Services/ConfigService.cs` — update client-side `AppConfig` record
- Reference: `Tests/Unit/WebChat/VapidConfigTests.cs`

**Implementation:**

In `WebChat/Program.cs`:
```csharp
// Update AppConfig record
internal record AppConfig(string AgentUrl, UserConfig[] Users, string? VapidPublicKey);

// Update /api/config endpoint
app.MapGet("/api/config", (IConfiguration config) =>
{
    var users = config.GetSection("Users").Get<UserConfig[]>() ?? [];
    var vapidPublicKey = config["WebPush:PublicKey"];
    return new AppConfig(
        config["AgentUrl"] ?? "http://localhost:5000",
        users,
        vapidPublicKey);
});
```

In `WebChat.Client/Services/ConfigService.cs`:
```csharp
public record AppConfig(string? AgentUrl, UserConfig[]? Users, string? VapidPublicKey);
```

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~VapidConfigTests"`
Expected: ALL tests PASS

**Commit:** `git commit -m "feat: expose VAPID public key via /api/config"`

---

### Task 5.3: Adversarial review of VAPID config exposure

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 5.2 must be complete

**Your role:** Adversarial reviewer.

**Design requirements to verify:**
- `GET /api/config` returns `VapidPublicKey` field
- When VAPID keys are configured, the public key value is returned
- When VAPID keys are NOT configured, `VapidPublicKey` is null
- Client-side `AppConfig` record matches server-side shape
- No private key is ever exposed (only public key)

**Review checklist:**
1. **Security** — Verify the private key is NEVER included in the config response. Search for any reference to `PrivateKey` in the WebChat project.
2. **Design compliance** — Both server and client `AppConfig` updated?
3. **Backward compatibility** — Does the JSON deserialization still work if the field is missing (e.g., client updated but server not yet)?
4. **Edge cases** — What if `WebPush:PublicKey` is an empty string?

**You MUST write and run additional tests.** Minimum: 3.

**What to produce:**
- Issues list, additional tests, verdict

**Commit additional tests:** `git commit -m "test: add adversarial tests for VAPID config exposure"`

---

## Triplet 6: Client Push Infrastructure

### Task 6.1: Write failing tests for client push infrastructure

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 5.3 (VAPID config must be available)

**Design requirements being tested:**
- Service worker handles `push` event: parses JSON payload, checks if any window is visible, shows notification only if not visible
- Service worker handles `notificationclick` event: closes notification, focuses existing window or opens new one
- `push-notifications.js` provides `requestPermission()`, `subscribe(vapidPublicKey)`, `getExistingSubscription()`, `unsubscribe()` functions
- Blazor `PushNotificationService` wraps JS interop for permission, subscribe, unsubscribe
- Bell icon UI toggles push subscription state

**Files:**
- Create: `Tests/Unit/WebChat.Client/Services/PushNotificationServiceTests.cs`

**What to test:**

The Blazor `PushNotificationService` can be tested by mocking `IJSRuntime` and `IChatConnectionService`. The service worker and JS interop module are JavaScript and tested via integration tests (Triplet 7).

```csharp
using Microsoft.JSInterop;
using Moq;
using Shouldly;
using WebChat.Client.Contracts;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class PushNotificationServiceTests
{
    private readonly Mock<IJSRuntime> _mockJsRuntime;
    private readonly Mock<IChatConnectionService> _mockConnectionService;
    private readonly PushNotificationService _sut;

    public PushNotificationServiceTests()
    {
        _mockJsRuntime = new Mock<IJSRuntime>();
        _mockConnectionService = new Mock<IChatConnectionService>();
        _sut = new PushNotificationService(_mockJsRuntime.Object, _mockConnectionService.Object);
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionGranted_ReturnsTrue()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .ReturnsAsync("granted");
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .ReturnsAsync(new PushSubscriptionResult("https://endpoint", "key", "auth"));

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionDenied_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .ReturnsAsync("denied");

        var result = await _sut.RequestAndSubscribeAsync("BPublicKey123");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task RequestAndSubscribeAsync_WhenPermissionGranted_SendsSubscriptionToHub()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<string>("pushNotifications.requestPermission", It.IsAny<object[]>()))
            .ReturnsAsync("granted");
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", It.IsAny<object[]>()))
            .ReturnsAsync(new PushSubscriptionResult("https://endpoint", "key", "auth"));

        await _sut.RequestAndSubscribeAsync("BPublicKey123");

        _mockConnectionService.Verify(c => c.HubConnection, Times.AtLeastOnce);
    }

    [Fact]
    public async Task UnsubscribeAsync_CallsJsUnsubscribe()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.unsubscribe", It.IsAny<object[]>()))
            .ReturnsAsync(true);

        await _sut.UnsubscribeAsync();

        _mockJsRuntime.Verify(js => js.InvokeAsync<bool>(
            "pushNotifications.unsubscribe", It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task IsSubscribedAsync_WhenSubscribed_ReturnsTrue()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.isSubscribed", It.IsAny<object[]>()))
            .ReturnsAsync(true);

        var result = await _sut.IsSubscribedAsync();

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task IsSubscribedAsync_WhenNotSubscribed_ReturnsFalse()
    {
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<bool>("pushNotifications.isSubscribed", It.IsAny<object[]>()))
            .ReturnsAsync(false);

        var result = await _sut.IsSubscribedAsync();

        result.ShouldBeFalse();
    }
}
```

**Note:** `PushSubscriptionResult` is a simple record that the JS interop returns. Define it alongside the service.

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~PushNotificationServiceTests"`
Expected: ALL tests FAIL (PushNotificationService doesn't exist yet)

**Commit:** `git commit -m "test: add failing tests for client PushNotificationService"`

---

### Task 6.2: Implement client push infrastructure

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 6.1 must be complete

**Goal:** Implement the full client-side push notification stack: service worker handlers, JS interop module, Blazor service, and UI bell toggle.

**Files:**
- Modify: `WebChat.Client/wwwroot/service-worker.js` — add push + notificationclick handlers
- Modify: `WebChat.Client/wwwroot/service-worker.published.js` — add push + notificationclick handlers (before existing code)
- Create: `WebChat.Client/wwwroot/push-notifications.js` — JS interop module
- Create: `WebChat.Client/Services/PushNotificationService.cs` — Blazor service
- Modify: `WebChat.Client/Program.cs` — register PushNotificationService
- Modify: `WebChat.Client/wwwroot/index.html` — add script reference for push-notifications.js
- Reference: `Tests/Unit/WebChat.Client/Services/PushNotificationServiceTests.cs`

**Service Worker handlers** (add to both `service-worker.js` and `service-worker.published.js`):

```js
self.addEventListener('push', event => {
    const data = event.data?.json() ?? { title: 'New message', body: '' };
    event.waitUntil(
        self.clients.matchAll({ type: 'window', includeUncontrolled: true })
            .then(clients => {
                const anyVisible = clients.some(c => c.visibilityState === 'visible');
                if (!anyVisible) {
                    return self.registration.showNotification(data.title, {
                        body: data.body,
                        icon: '/icon.svg',
                        data: { url: data.url }
                    });
                }
            })
    );
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = event.notification.data?.url ?? '/';
    event.waitUntil(
        self.clients.matchAll({ type: 'window' }).then(clients => {
            const existing = clients.find(c => c.url.includes(url));
            return existing ? existing.focus() : self.clients.openWindow(url);
        })
    );
});
```

**JS Interop Module** (`wwwroot/push-notifications.js`):

```js
window.pushNotifications = {
    async requestPermission() {
        if (!('Notification' in window)) return 'denied';
        return await Notification.requestPermission();
    },

    async subscribe(vapidPublicKey) {
        const registration = await navigator.serviceWorker.ready;
        const applicationServerKey = this._urlBase64ToUint8Array(vapidPublicKey);
        const subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey
        });
        const json = subscription.toJSON();
        return {
            endpoint: json.endpoint,
            p256dh: json.keys.p256dh,
            auth: json.keys.auth
        };
    },

    async isSubscribed() {
        if (!('serviceWorker' in navigator)) return false;
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();
        return subscription !== null;
    },

    async unsubscribe() {
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();
        if (subscription) {
            return await subscription.unsubscribe();
        }
        return false;
    },

    _urlBase64ToUint8Array(base64String) {
        const padding = '='.repeat((4 - base64String.length % 4) % 4);
        const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        const rawData = window.atob(base64);
        return Uint8Array.from([...rawData].map(char => char.charCodeAt(0)));
    }
};
```

**Blazor PushNotificationService:**

```csharp
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public record PushSubscriptionResult(string Endpoint, string P256dh, string Auth);

public sealed class PushNotificationService(IJSRuntime jsRuntime, IChatConnectionService connectionService)
{
    public async Task<bool> RequestAndSubscribeAsync(string vapidPublicKey)
    {
        var permission = await jsRuntime.InvokeAsync<string>("pushNotifications.requestPermission");
        if (permission != "granted") return false;

        var result = await jsRuntime.InvokeAsync<PushSubscriptionResult>("pushNotifications.subscribe", vapidPublicKey);
        if (result is null) return false;

        var subscription = new PushSubscriptionDto(result.Endpoint, result.P256dh, result.Auth);
        if (connectionService.HubConnection is not null)
        {
            await connectionService.HubConnection.InvokeAsync("SubscribePush", subscription);
        }

        return true;
    }

    public async Task UnsubscribeAsync()
    {
        var wasSubscribed = await jsRuntime.InvokeAsync<bool>("pushNotifications.unsubscribe");
        if (wasSubscribed && connectionService.HubConnection is not null)
        {
            // Best effort to notify server
            try
            {
                await connectionService.HubConnection.InvokeAsync("UnsubscribePush", "");
            }
            catch
            {
                // Ignore — subscription is already removed client-side
            }
        }
    }

    public async Task<bool> IsSubscribedAsync()
    {
        return await jsRuntime.InvokeAsync<bool>("pushNotifications.isSubscribed");
    }
}
```

**UI Bell Component:** Create a small Blazor component or add the bell toggle to the existing header component. The bell should call `PushNotificationService.RequestAndSubscribeAsync()` or `UnsubscribeAsync()` on click, and use `IsSubscribedAsync()` to show the correct state.

**DI Registration** in `WebChat.Client/Program.cs`:
```csharp
builder.Services.AddScoped<PushNotificationService>();
```

**Script reference** in `index.html`:
```html
<script src="push-notifications.js"></script>
```

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~PushNotificationServiceTests"`
Expected: ALL tests PASS

**Commit:** `git commit -m "feat: implement client push notification infrastructure"`

---

### Task 6.3: Adversarial review of client push infrastructure

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 6.2 must be complete

**Your role:** Adversarial reviewer.

**Design requirements to verify:**
- Service worker suppresses notifications when any app window is visible
- Service worker shows notification with title, body, icon when no window is visible
- Notification click focuses existing window or opens new one
- JS interop handles: requestPermission, subscribe with VAPID key, check subscription, unsubscribe
- Base64url to Uint8Array conversion is correct (VAPID public key encoding)
- Blazor service requests permission → subscribes → sends to hub
- Blazor service unsubscribes via JS and notifies hub
- Bell UI reflects subscription state
- push-notifications.js script loaded in index.html
- PushNotificationService registered in DI

**Review checklist:**
1. **Service worker correctness** — Does `matchAll` with `includeUncontrolled: true` work correctly? Is `visibilityState === 'visible'` the right check?
2. **JS interop safety** — What if `navigator.serviceWorker` is undefined? What if `pushManager` is not available?
3. **Base64url conversion** — Test with known VAPID keys to verify the conversion
4. **Error handling** — What if JS interop throws? What if hub connection is null?
5. **Script loading order** — Is `push-notifications.js` loaded before Blazor tries to call it?

**You MUST write and run additional tests.** Minimum: 3. Suggested:
- Test `RequestAndSubscribeAsync` when JS interop throws
- Test `UnsubscribeAsync` when hub connection is null
- Test that service is registered in DI correctly

**What to produce:**
- Issues list, additional tests, verdict

**Commit additional tests:** `git commit -m "test: add adversarial tests for client push infrastructure"`

---

## Triplet 7: End-to-End Integration

### Task 7.1: Write failing integration tests

**Type:** RED (Test Writing)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** ALL previous triplets must be complete (Tasks 1.3, 2.3, 3.3, 4.3, 5.3, 6.3)

**Design requirements being tested:**
- Full flow: client subscribes to push → agent completes response → push notification sent to subscription endpoint
- Push subscription persists across hub reconnection
- Unsubscribe stops push notifications

**Files:**
- Create: `Tests/Integration/WebChat/PushNotificationIntegrationTests.cs`

**What to test:**

Use the `WebChatServerFixture` which provides a real SignalR server with Redis. Test the backend flow end-to-end (client subscribes via hub, trigger stream completed, verify push service was called).

```csharp
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.WebChat;

public sealed class PushNotificationIntegrationTests(WebChatServerFixture fixture)
    : IClassFixture<WebChatServerFixture>, IAsyncLifetime
{
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = fixture.CreateHubConnection();
        await _connection.StartAsync();
        await _connection.InvokeAsync("RegisterUser", "push-integration-user");
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task SubscribePush_ThenGetAll_ReturnsSubscription()
    {
        var subscription = new PushSubscriptionDto(
            $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}",
            "BNcRdreALRFXTkOOUHK1EtK2wtaz...",
            "tBHItJI5svbpC7sc9d8M2w==");

        await _connection.InvokeAsync("SubscribePush", subscription);

        // Verify via the store directly (fixture exposes services)
        var store = fixture.GetService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        all.ShouldContain(x =>
            x.UserId == "push-integration-user" &&
            x.Subscription.Endpoint == subscription.Endpoint);
    }

    [Fact]
    public async Task UnsubscribePush_RemovesSubscription()
    {
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var subscription = new PushSubscriptionDto(endpoint, "key", "auth");

        await _connection.InvokeAsync("SubscribePush", subscription);
        await _connection.InvokeAsync("UnsubscribePush", endpoint);

        var store = fixture.GetService<IPushSubscriptionStore>();
        var all = await store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == endpoint);
    }
}
```

**Note:** The `WebChatServerFixture` may need a `GetService<T>()` helper method to expose registered services for verification. Add it if not present.

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~PushNotificationIntegrationTests"`
Expected: Tests FAIL until push infrastructure is fully wired in the fixture

**Commit:** `git commit -m "test: add failing integration tests for push notification flow"`

---

### Task 7.2: Fix integration failures

**Type:** GREEN (Implementation)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 7.1 must be complete

**Goal:** Make all integration tests pass. This may involve:
- Adding `GetService<T>()` to `WebChatServerFixture`
- Ensuring `IPushSubscriptionStore` and `IPushNotificationService` are registered in the test fixture
- Fixing any wiring issues discovered during integration

**Verification:**
Run: `dotnet test Tests/ --filter "FullyQualifiedName~PushNotificationIntegrationTests"`
Expected: ALL tests PASS

Also run the full test suite: `dotnet test Tests/`
Expected: ALL tests PASS (no regressions)

**Commit:** `git commit -m "feat: wire push notification integration and fix test fixture"`

---

### Task 7.3: Final adversarial review

**Type:** REVIEW (Adversarial)
**Dispatch as:** Fresh subagent via Task tool
**Depends on:** Task 7.2 must be complete

**Your role:** Final adversarial reviewer. Review the ENTIRE push notification implementation against ALL design requirements.

**Design requirements checklist (from design doc):**

1. [ ] VAPID keys stored in .NET User Secrets (`WebPush:PublicKey`, `WebPush:PrivateKey`, `WebPush:Subject`)
2. [ ] VAPID public key exposed via `GET /api/config`
3. [ ] `PushSubscriptionDto` record with Endpoint, P256dh, Auth
4. [ ] `IPushSubscriptionStore` with Save, Remove, RemoveByEndpoint, GetAll
5. [ ] `RedisPushSubscriptionStore` using Redis hash (key `push:subs:{userId}`)
6. [ ] `IPushNotificationService` with `SendToSpaceAsync`
7. [ ] `WebPushNotificationService` using WebPush NuGet library
8. [ ] HTTP 410 Gone → removes expired subscription
9. [ ] Errors logged but don't throw/block
10. [ ] `HubNotifier` triggers push on `StreamChangeType.Completed` only
11. [ ] `ChatHub.SubscribePush` and `UnsubscribePush` methods
12. [ ] Both require user registration
13. [ ] DI registers all push services
14. [ ] `NullPushNotificationService` when VAPID not configured
15. [ ] Service worker `push` event: suppresses when window visible
16. [ ] Service worker `notificationclick`: focus or open window
17. [ ] JS interop: requestPermission, subscribe, isSubscribed, unsubscribe
18. [ ] Blazor `PushNotificationService` wrapping JS interop
19. [ ] Bell icon UI for toggling notifications
20. [ ] `push-notifications.js` loaded in `index.html`

**Review the full diff:** `git diff master..HEAD` to see all changes.

**Run the complete test suite:** `dotnet test Tests/`

**You MUST write and run additional tests.** Minimum: 3 integration-level tests that exercise cross-component behavior.

**What to produce:**
- Checklist with PASS/FAIL for each requirement
- Issues list (Critical / Important / Minor)
- Additional tests and results
- Final verdict: PASS or FAIL

**Commit additional tests:** `git commit -m "test: add final adversarial integration tests for push notifications"`

---

## Dependency Graph

```
Task 0 (Scaffolding)
├── Triplet 1 (Redis Store): 1.1 → 1.2 → 1.3
│   ├── Triplet 2 (Push Service): 2.1 → 2.2 → 2.3
│   │   └── Triplet 3 (HubNotifier): 3.1 → 3.2 → 3.3
│   └── Triplet 4 (ChatHub + DI): 4.1 → 4.2 → 4.3
└── Triplet 5 (Config): 5.1 → 5.2 → 5.3
    └── Triplet 6 (Client): 6.1 → 6.2 → 6.3

All above ──→ Triplet 7 (Integration): 7.1 → 7.2 → 7.3
```

**Parallel opportunities:**
- Triplets 1 and 5 can run in parallel (after Task 0)
- Triplet 4 can start after Triplet 1 completes (parallel with Triplets 2, 3)
- Triplet 6 can start after Triplet 5 completes (parallel with Triplets 2, 3, 4)

---

## Execution Instructions

**Recommended:** Execute using subagents for fresh context per task.

For each task, dispatch a fresh subagent using the Task tool:
- subagent_type: "general-purpose"
- Provide the FULL task text in the prompt (don't make subagent read this file)
- Include relevant context from earlier tasks (what was built, where files are)

**Execution order:**
- Tasks within a triplet are strictly sequential: N.1 → N.2 → N.3
- Independent triplets MAY run in parallel if they touch different files
- Dependent triplets are sequential: complete triplet N before starting triplet M

**Never:**
- Skip a test-writing task (N.1) — "I'll write tests with the implementation"
- Skip an adversarial review task (N.3) — "The tests already pass, it's fine"
- Combine tasks within a triplet — each is a separate subagent dispatch
- Proceed to N.2 if N.1 tests don't compile/exist
- Proceed to N.3 if N.2 tests don't pass
- Proceed to next triplet if N.3 verdict is FAIL
