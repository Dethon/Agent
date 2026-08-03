using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public static class RedisConnection
{
    // A filesystem server resolves its backend when the tool list is built, and for the two
    // Redis-backed servers that reaches the store. Retry in the background rather than failing
    // server construction outright when Redis happens to be slow to come up.
    public static IConnectionMultiplexer ConnectResiliently(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    }
}