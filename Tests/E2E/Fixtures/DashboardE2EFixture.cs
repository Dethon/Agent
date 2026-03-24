using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Tests.E2E.Fixtures;

public class DashboardE2EFixture : E2EFixtureBase
{
    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _observability;

    public string DashboardUrl { get; private set; } = "";
    public string RedisConnectionString { get; private set; } = "";

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        var solutionRoot = TestHelpers.FindSolutionRoot();

        _network = new NetworkBuilder()
            .WithName($"e2e-dashboard-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync(ct);

        // 1. Build base-sdk image
        var baseSdkImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Dockerfile.base-sdk")
            .WithName("base-sdk:latest")
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await baseSdkImage.CreateAsync(ct);

        // 2. Start Redis
        _redis = new ContainerBuilder("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6379))
            .Build();
        await _redis.StartAsync(ct);

        // 3. Build Observability image
        var observabilityImageName = $"observability-e2e-{Guid.NewGuid():N}";
        var observabilityImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(solutionRoot)
            .WithDockerfile("Observability/Dockerfile")
            .WithName(observabilityImageName)
            .WithDeleteIfExists(false)
            .WithCleanUp(false)
            .Build();
        await observabilityImage.CreateAsync(ct);

        // 4. Start Observability
        _observability = new ContainerBuilder(observabilityImage)
            .WithNetwork(_network)
            .WithNetworkAliases("observability")
            .WithPortBinding(8080, true)
            .WithEnvironment("REDIS__CONNECTIONSTRING", "redis:6379")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/api/metrics/health")))
            .Build();
        await _observability.StartAsync(ct);

        var host = _observability.Hostname;
        var port = _observability.GetMappedPublicPort(8080);
        DashboardUrl = $"http://{host}:{port}/";

        var redisPort = _redis.GetMappedPublicPort(6379);
        RedisConnectionString = $"{_redis.Hostname}:{redisPort}";
    }

    protected override async Task StopContainersAsync()
    {
        if (_observability is not null) await _observability.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }
}

[CollectionDefinition("DashboardE2E")]
public class DashboardE2ECollection : ICollectionFixture<DashboardE2EFixture>;
