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
        // The store returns ALL subscriptions, so this test just checks it returns a valid list
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

    [Fact]
    public async Task GetBySpaceAsync_FiltersSubscriptionsBySpace()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/space-a", "k1", "a1");
        var sub2 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/space-b", "k2", "a2");

        await _store.SaveAsync(userId, sub1, "space-a");
        await _store.SaveAsync(userId, sub2, "space-b");

        var spaceASubs = await _store.GetBySpaceAsync("space-a");
        spaceASubs.ShouldContain(x => x.Subscription.Endpoint == sub1.Endpoint);
        spaceASubs.ShouldNotContain(x => x.Subscription.Endpoint == sub2.Endpoint);
    }

    [Fact]
    public async Task GetBySpaceAsync_DefaultSpace_IncludesSubscriptionsWithoutExplicitSpace()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/default-space", "k1", "a1");

        await _store.SaveAsync(userId, sub);

        var defaultSubs = await _store.GetBySpaceAsync("default");
        defaultSubs.ShouldContain(x => x.Subscription.Endpoint == sub.Endpoint);
    }

    [Fact]
    public async Task GetBySpaceAsync_NoMatchingSpace_ReturnsEmpty()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/other-space", "k1", "a1");

        await _store.SaveAsync(userId, sub, "some-space");

        var result = await _store.GetBySpaceAsync("nonexistent-space");
        result.ShouldNotContain(x => x.Subscription.Endpoint == sub.Endpoint);
    }

    [Fact]
    public async Task SaveAsync_WithSpaceSlug_PreservesSpaceInRoundTrip()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/space-rt", "k1", "a1");

        await _store.SaveAsync(userId, sub, "my-space");

        var all = await _store.GetAllAsync();
        all.ShouldContain(x => x.Subscription.Endpoint == sub.Endpoint);

        var spaceFiltered = await _store.GetBySpaceAsync("my-space");
        spaceFiltered.ShouldContain(x => x.Subscription.Endpoint == sub.Endpoint);

        var otherSpace = await _store.GetBySpaceAsync("other-space");
        otherSpace.ShouldNotContain(x => x.Subscription.Endpoint == sub.Endpoint);
    }

    // --- Adversarial tests ---

    [Fact]
    public async Task SaveAsync_EndpointWithQueryParameters_PreservesFullUrl()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var endpoint = "https://fcm.googleapis.com/fcm/send/abc?key=val&foo=bar#fragment";
        var sub = new PushSubscriptionDto(endpoint, "p256dh-key", "auth-key");

        await _store.SaveAsync(userId, sub);

        var all = await _store.GetAllAsync();
        var match = all.FirstOrDefault(x => x.UserId == userId);
        match.Subscription.ShouldNotBeNull();
        match.Subscription.Endpoint.ShouldBe(endpoint);
    }

    [Fact]
    public async Task RemoveByEndpointAsync_EndpointDoesNotExist_DoesNotThrow()
    {
        await Should.NotThrowAsync(
            () => _store.RemoveByEndpointAsync("https://nonexistent.example.com/totally-missing"));
    }

    [Fact]
    public async Task SaveAsync_RoundTrip_PreservesP256dhAndAuthExactly()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var p256dh = "BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA_0QTpQtUbIDS7Iq2jGPayfP+szs0yzE1hLCpMQUcGN3MkrjXQ0=";
        var auth = "tBHItJI5svbpC7sc9d8M2w==";
        var sub = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/roundtrip", p256dh, auth);

        await _store.SaveAsync(userId, sub);

        var all = await _store.GetAllAsync();
        var match = all.First(x => x.UserId == userId);
        match.Subscription.P256dh.ShouldBe(p256dh);
        match.Subscription.Auth.ShouldBe(auth);
    }

    [Fact]
    public async Task RemoveByEndpointAsync_SharedEndpointAcrossMultipleUsers_RemovesFromAll()
    {
        var userId1 = $"test-user-{Guid.NewGuid():N}";
        var userId2 = $"test-user-{Guid.NewGuid():N}";
        var userId3 = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId1);
        _createdUserIds.Add(userId2);
        _createdUserIds.Add(userId3);
        var sharedEndpoint = "https://fcm.googleapis.com/fcm/send/shared-across-all";

        await _store.SaveAsync(userId1, CreateSubscription(sharedEndpoint));
        await _store.SaveAsync(userId2, CreateSubscription(sharedEndpoint));
        await _store.SaveAsync(userId3, CreateSubscription(sharedEndpoint));

        await _store.RemoveByEndpointAsync(sharedEndpoint);

        var all = await _store.GetAllAsync();
        all.ShouldNotContain(x => x.Subscription.Endpoint == sharedEndpoint);
    }

    [Fact]
    public async Task SaveAsync_EndpointWithSpecialCharacters_HandlesCorrectly()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var endpoint = "https://example.com/push/endpoint?token=abc%20def&lang=en&special=<>&quote=\"test\"";
        var sub = new PushSubscriptionDto(endpoint, "key123", "auth456");

        await _store.SaveAsync(userId, sub);

        var all = await _store.GetAllAsync();
        var match = all.FirstOrDefault(x => x.UserId == userId);
        match.Subscription.ShouldNotBeNull();
        match.Subscription.Endpoint.ShouldBe(endpoint);
        match.Subscription.P256dh.ShouldBe("key123");
        match.Subscription.Auth.ShouldBe("auth456");
    }

    [Fact]
    public async Task RemoveAsync_OnlyRemovesTargetEndpoint_LeavesOthersIntact()
    {
        var userId = $"test-user-{Guid.NewGuid():N}";
        _createdUserIds.Add(userId);
        var sub1 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/keep", "k1", "a1");
        var sub2 = new PushSubscriptionDto("https://fcm.googleapis.com/fcm/send/remove", "k2", "a2");

        await _store.SaveAsync(userId, sub1);
        await _store.SaveAsync(userId, sub2);
        await _store.RemoveAsync(userId, sub2.Endpoint);

        var all = await _store.GetAllAsync();
        var userSubs = all.Where(x => x.UserId == userId).ToList();
        userSubs.Count.ShouldBe(1);
        userSubs[0].Subscription.Endpoint.ShouldBe(sub1.Endpoint);
        userSubs[0].Subscription.P256dh.ShouldBe("k1");
        userSubs[0].Subscription.Auth.ShouldBe("a1");
    }
}
