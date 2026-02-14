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
        var json = System.Text.Json.JsonSerializer.Serialize(new OAuthTokens
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
}
