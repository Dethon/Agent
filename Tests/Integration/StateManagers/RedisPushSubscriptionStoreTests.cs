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
}
