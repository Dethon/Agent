using System.Diagnostics;
using System.Net.Sockets;
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
public class JonasInstantiationBenchmark(RedisFixture redisFixture, ITestOutputHelper output)
    : IClassFixture<RedisFixture>
{
    private const string AgentId = "jonas";
    private const string EndpointsOverrideEnvVar = "BENCHMARK_MCP_ENDPOINTS";
    private const int WarmupIterations = 1;
    private const int MeasuredIterations = 5;
    private static readonly TimeSpan _iterationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _reachabilityProbeTimeout = TimeSpan.FromMilliseconds(500);

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

        var configuredEndpoints = _configuration
            .GetSection("agents")
            .GetChildren()
            .First(c => c["id"] == AgentId)
            .GetSection("mcpServerEndpoints")
            .Get<string[]>()
            ?? throw new InvalidOperationException($"agent '{AgentId}' has no mcpServerEndpoints");

        // The appsettings entries use docker-internal hostnames; running outside the compose
        // network requires overriding with reachable URLs (e.g., host-mapped localhost ports).
        var endpoints = ResolveEffectiveEndpoints(configuredEndpoints);

        Skip.IfNot(await AllEndpointsReachable(endpoints, _reachabilityProbeTimeout),
            $"One or more jonas MCP endpoints unreachable: {string.Join(", ", endpoints)}");

        var (provider, factory) = BuildFactory(endpoints);
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

    private (ServiceProvider Provider, IAgentFactory Factory) BuildFactory(string[] jonasEndpoints)
    {
        var settings = _configuration.Get<Agent.Settings.AgentSettings>()
            ?? throw new InvalidOperationException("Failed to bind AgentSettings from configuration");

        settings = settings with
        {
            Redis = settings.Redis with { ConnectionString = redisFixture.ConnectionString },
            Agents = settings.Agents
                .Select(a => a.Id.Equals(AgentId, StringComparison.OrdinalIgnoreCase)
                    ? a with { McpServerEndpoints = jonasEndpoints }
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
            await agent.DisposeThreadSessionAsync(session);
        }
        finally
        {
            await agent.DisposeAsync();
        }
    }

    private static string[] ResolveEffectiveEndpoints(string[] configured)
    {
        var overrideValue = Environment.GetEnvironmentVariable(EndpointsOverrideEnvVar);
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return configured;
        }

        var parsed = overrideValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parsed.Length != configured.Length)
        {
            throw new InvalidOperationException(
                $"{EndpointsOverrideEnvVar} has {parsed.Length} entries but jonas is configured with " +
                $"{configured.Length} MCP servers; the override must list one URL per configured server.");
        }

        return parsed;
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

    private static async Task<bool> AllEndpointsReachable(string[] endpoints, TimeSpan timeout)
    {
        var probes = endpoints.Select(ep => IsReachable(ep, timeout));
        var results = await Task.WhenAll(probes);
        return results.All(r => r);
    }

    private static async Task<bool> IsReachable(string endpoint, TimeSpan timeout)
    {
        try
        {
            var uri = new Uri(endpoint);
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
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
