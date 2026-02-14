using Domain.DTOs;
using Infrastructure.Calendar;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Calendar;

public class RedisCalendarTokenStoreIntegrationTests(RedisFixture redis)
    : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task StoreAndRetrieveTokens_RoundTrip()
    {
        var store = new RedisCalendarTokenStore(redis.Connection);
        var tokens = new OAuthTokens
        {
            AccessToken = "access-token-123",
            RefreshToken = "refresh-token-456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await store.StoreTokensAsync("user-1", tokens);
        var retrieved = await store.GetTokensAsync("user-1");

        retrieved.ShouldNotBeNull();
        retrieved.AccessToken.ShouldBe("access-token-123");
        retrieved.RefreshToken.ShouldBe("refresh-token-456");
    }

    [Fact]
    public async Task GetTokens_WhenNoneStored_ReturnsNull()
    {
        var store = new RedisCalendarTokenStore(redis.Connection);

        var result = await store.GetTokensAsync("nonexistent-user");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RemoveTokens_DeletesStoredTokens()
    {
        var store = new RedisCalendarTokenStore(redis.Connection);
        var tokens = new OAuthTokens
        {
            AccessToken = "a", RefreshToken = "r",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await store.StoreTokensAsync("user-rm", tokens);
        await store.RemoveTokensAsync("user-rm");
        var result = await store.GetTokensAsync("user-rm");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task HasTokens_WhenStored_ReturnsTrue()
    {
        var store = new RedisCalendarTokenStore(redis.Connection);
        var tokens = new OAuthTokens
        {
            AccessToken = "a", RefreshToken = "r",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await store.StoreTokensAsync("user-has", tokens);
        var result = await store.HasTokensAsync("user-has");

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task StoreTokens_OverwritesExisting()
    {
        var store = new RedisCalendarTokenStore(redis.Connection);
        var tokens1 = new OAuthTokens
        {
            AccessToken = "old", RefreshToken = "old-r",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var tokens2 = new OAuthTokens
        {
            AccessToken = "new", RefreshToken = "new-r",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
        };

        await store.StoreTokensAsync("user-ow", tokens1);
        await store.StoreTokensAsync("user-ow", tokens2);
        var result = await store.GetTokensAsync("user-ow");

        result.ShouldNotBeNull();
        result.AccessToken.ShouldBe("new");
        result.RefreshToken.ShouldBe("new-r");
    }
}
