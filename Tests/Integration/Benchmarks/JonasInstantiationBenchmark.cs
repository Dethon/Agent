using System.Diagnostics;
using Agent.Modules;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Benchmarks;

[Trait("Category", "Benchmark")]
public class JonasInstantiationBenchmark(
    RedisFixture redisFixture,
    JonasMcpStackFixture mcpStackFixture,
    ITestOutputHelper output)
    : IClassFixture<RedisFixture>, IClassFixture<JonasMcpStackFixture>
{
    private const string AgentId = "jonas";
    private const int WarmupIterations = 1;
    private const int MeasuredIterations = 5;
    private static readonly TimeSpan _iterationTimeout = TimeSpan.FromMinutes(2);

    private static readonly IConfigurationRoot _configuration = new ConfigurationBuilder()
        .AddJsonFile(LocateAgentAppSettings(), optional: false)
        .AddUserSecrets<JonasInstantiationBenchmark>()
        .AddEnvironmentVariables()
        .Build();

    [SkippableFact]
    public async Task CreateJonasAgent_FiveMeasuredIterations_CompletesUnderTimeout()
    {
        Skip.If(string.IsNullOrEmpty(_configuration["openRouter:apiKey"]),
            "openRouter:apiKey not set in user secrets");

        var (provider, factory) = BuildFactory();
        try
        {
            var approvalHandler = new AutoApproveHandler();
            var userId = $"benchmark-user-{Guid.NewGuid()}";

            // Warmup — absorbs JIT, HttpClient pool init, TLS handshakes, Redis warm-up.
            for (var i = 0; i < WarmupIterations; i++)
            {
                await RunOneIterationAsync(factory, approvalHandler, userId);
            }

            var measured = new List<long>(MeasuredIterations);
            var stopwatch = new Stopwatch();
            for (var i = 0; i < MeasuredIterations; i++)
            {
                stopwatch.Restart();
                await RunOneIterationAsync(factory, approvalHandler, userId);
                stopwatch.Stop();
                measured.Add(stopwatch.ElapsedMilliseconds);
                output.WriteLine($"[iteration {i + 1}] {stopwatch.ElapsedMilliseconds} ms");
            }

            var min = measured.Min();
            var max = measured.Max();
            var mean = (long)measured.Average();
            output.WriteLine(
                $"[summary] iterations={MeasuredIterations} min={min} ms mean={mean} ms max={max} ms");

            measured.Count.ShouldBe(MeasuredIterations);
            measured.ShouldAllBe(t => t < _iterationTimeout.TotalMilliseconds);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    private (ServiceProvider Provider, IAgentFactory Factory) BuildFactory()
    {
        var settings = _configuration.Get<Agent.Settings.AgentSettings>()
            ?? throw new InvalidOperationException("Failed to bind AgentSettings from configuration");

        var stubEndpoints = mcpStackFixture.Endpoints.ToArray();

        // Replace jonas's docker-internal MCP endpoints with the in-process stub stack so the
        // benchmark is self-contained. Model, features, and the rest of the definition are
        // preserved.
        settings = settings with
        {
            Redis = settings.Redis with { ConnectionString = redisFixture.ConnectionString },
            Agents = settings.Agents
                .Select(a => a.Id.Equals(AgentId, StringComparison.OrdinalIgnoreCase)
                    ? a with { McpServerEndpoints = stubEndpoints }
                    : a)
                .ToArray()
        };

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_configuration);
        services.AddLogging();

        services
            .AddAgent(settings)
            .AddScheduling()
            .AddSubAgents(settings.SubAgents)
            .AddMemory(_configuration);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IAgentFactory>();
        return (provider, factory);
    }

    private static async Task RunOneIterationAsync(
        IAgentFactory factory, IToolApprovalHandler approvalHandler, string userId)
    {
        using var cts = new CancellationTokenSource(_iterationTimeout);
        var agentKey = new AgentKey($"benchmark:{Guid.NewGuid()}", AgentId);
        var agent = factory.Create(agentKey, userId, AgentId, approvalHandler);
        try
        {
            var session = await agent.CreateSessionAsync(cts.Token);
            // WarmupSessionAsync is the step that actually opens the MCP connections —
            // CreateSessionAsync alone only allocates a placeholder thread.
            await agent.WarmupSessionAsync(session, cts.Token);
            await agent.DisposeThreadSessionAsync(session);
        }
        finally
        {
            await agent.DisposeAsync();
        }
    }

    private static string LocateAgentAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Agent", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate Agent/appsettings.json by walking up from test bin directory");
    }
}

file sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
