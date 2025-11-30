using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Storage;
using StackExchange.Redis;

namespace Tests.Integration.Fixtures;

public class RedisFixture : IAsyncLifetime
{
    private const int RedisPort = 6379;

    private IContainer _container = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;
    public RedisConversationHistoryStore Store { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(RedisPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilExternalTcpPortIsAvailable(RedisPort))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(RedisPort);
        ConnectionString = $"{host}:{port}";

        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
        Store = new RedisConversationHistoryStore(Connection, TimeSpan.FromMinutes(5));
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}