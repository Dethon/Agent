using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;

namespace Tests.Integration.Fixtures;

public class RedisFixture : IAsyncLifetime
{
    private const int RedisPort = 6379;
    private IContainer _container = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Readiness is the port, not the log line. Redis logs "Ready to accept connections"
        // within a second of starting, so under load the first log poll can begin after the
        // line was already written and then wait for it forever — a hang with no timeout,
        // which is what a full-suite run hit. Every other Redis container here waits on the
        // port.
        _container = new ContainerBuilder("redis/redis-stack:latest")
            .WithPortBinding(RedisPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(RedisPort))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(RedisPort);
        ConnectionString = $"{host}:{port}";

        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}