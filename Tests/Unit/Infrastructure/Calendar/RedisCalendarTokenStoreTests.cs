using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Calendar;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Calendar;

public class RedisCalendarTokenStoreTests
{
    private readonly Mock<IDatabase> _dbMock = new();
    private readonly RedisCalendarTokenStore _store;

    public RedisCalendarTokenStoreTests()
    {
        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_dbMock.Object);
        _store = new RedisCalendarTokenStore(redisMock.Object);
    }

    [Fact]
    public async Task StoreTokensAsync_WritesToRedisWithCorrectKey()
    {
        var tokens = new OAuthTokens
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await _store.StoreTokensAsync("user-1", tokens);

        _dbMock.Verify(db => db.StringSetAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task GetTokensAsync_WhenKeyDoesNotExist_ReturnsNull()
    {
        _dbMock.Setup(db => db.StringGetAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var result = await _store.GetTokensAsync("user-1");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetTokensAsync_WhenKeyExists_DeserializesAndReturnsTokens()
    {
        var json = JsonSerializer.Serialize(new OAuthTokens
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            ExpiresAt = DateTimeOffset.Parse("2026-03-01T00:00:00+00:00")
        });

        _dbMock.Setup(db => db.StringGetAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue(json));

        var result = await _store.GetTokensAsync("user-1");

        result.ShouldNotBeNull();
        result.AccessToken.ShouldBe("access-123");
        result.RefreshToken.ShouldBe("refresh-456");
    }

    [Fact]
    public async Task RemoveTokensAsync_DeletesKey()
    {
        await _store.RemoveTokensAsync("user-1");

        _dbMock.Verify(db => db.KeyDeleteAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task HasTokensAsync_WhenKeyExists_ReturnsTrue()
    {
        _dbMock.Setup(db => db.KeyExistsAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var result = await _store.HasTokensAsync("user-1");

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task HasTokensAsync_WhenKeyDoesNotExist_ReturnsFalse()
    {
        _dbMock.Setup(db => db.KeyExistsAsync(
            It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-1"),
            It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);

        var result = await _store.HasTokensAsync("user-1");

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task StoreAndGet_RoundTrip_PreservesAllFields()
    {
        var original = new OAuthTokens
        {
            AccessToken = "access-roundtrip",
            RefreshToken = "refresh-roundtrip",
            ExpiresAt = DateTimeOffset.Parse("2026-06-15T14:30:00+02:00")
        };

        RedisValue capturedValue = RedisValue.Null;
        _dbMock.Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>(
                (_, value, _, _, _, _) => capturedValue = value)
            .ReturnsAsync(true);

        await _store.StoreTokensAsync("user-rt", original);

        _dbMock.Setup(db => db.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-rt"),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(capturedValue);

        var retrieved = await _store.GetTokensAsync("user-rt");

        retrieved.ShouldNotBeNull();
        retrieved.AccessToken.ShouldBe(original.AccessToken);
        retrieved.RefreshToken.ShouldBe(original.RefreshToken);
        retrieved.ExpiresAt.ShouldBe(original.ExpiresAt);
    }

    [Fact]
    public async Task StoreTokensAsync_OverwritesExistingTokens_UsesWhenAlways()
    {
        var tokens = new OAuthTokens
        {
            AccessToken = "new-access",
            RefreshToken = "new-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await _store.StoreTokensAsync("user-1", tokens);

        _dbMock.Verify(db => db.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<bool>(),
            When.Always,
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task GetTokensAsync_WhenRedisContainsMalformedJson_ThrowsJsonException()
    {
        _dbMock.Setup(db => db.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == "calendar:tokens:user-corrupt"),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(new RedisValue("{not valid json!!!"));

        await Should.ThrowAsync<JsonException>(
            () => _store.GetTokensAsync("user-corrupt"));
    }

    [Fact]
    public async Task StoreTokensAsync_SetsTtlTo90Days()
    {
        var tokens = new OAuthTokens
        {
            AccessToken = "a",
            RefreshToken = "r",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        await _store.StoreTokensAsync("user-ttl", tokens);

        _dbMock.Verify(db => db.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            TimeSpan.FromDays(90),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }
}
