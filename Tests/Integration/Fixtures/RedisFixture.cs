using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Storage;
using StackExchange.Redis;

namespace Tests.Integration.Fixtures;

public class RedisFixture : IAsyncLifetime
{
    private const int RedisPort = 6379;

    private IContainer _container = null!;
    private IConnectionMultiplexer _connection = null!;

    public RedisConversationHistoryStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(RedisPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(RedisPort))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(RedisPort);
        var connectionString = $"{host}:{port}";

        _connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        Store = new RedisConversationHistoryStore(_connection, TimeSpan.FromMinutes(5));
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}