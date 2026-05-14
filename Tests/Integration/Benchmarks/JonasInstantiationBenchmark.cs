using System.Diagnostics;
using System.Runtime.CompilerServices;
using Agent.Modules;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Infrastructure.Metrics;
using Microsoft.Extensions.AI;
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
        .Build();

    [Fact]
    public async Task CreateJonasAgent_FiveMeasuredIterations_CompletesUnderTimeout()
    {
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

        // Replace jonas's docker-internal MCP endpoints with the test-managed container URLs
        // so the agent connects to the JonasMcpStackFixture stack rather than the production
        // compose network.
        var stackEndpoints = mcpStackFixture.Endpoints.ToArray();
        settings = settings with
        {
            Redis = settings.Redis with { ConnectionString = redisFixture.ConnectionString },
            Agents = settings.Agents
                .Select(a => a.Id.Equals(AgentId, StringComparison.OrdinalIgnoreCase)
                    ? a with { McpServerEndpoints = stackEndpoints }
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

        var openRouterConfig = new OpenRouterConfig
        {
            ApiUrl = settings.OpenRouter.ApiUrl,
            ApiKey = settings.OpenRouter.ApiKey,
            MaxContextTokens = settings.OpenRouter.MaxContextTokens
        };

        // Replace MultiAgentFactory's OpenRouter chat client with a stub so we don't make real
        // LLM calls during the benchmark. Everything else — definition lookup, tool resolution,
        // ToolApprovalChatClient wrapping, McpAgent construction, MCP discovery — is real.
        var factory = new MultiAgentFactory(
            provider,
            provider.GetRequiredService<IAgentDefinitionProvider>(),
            openRouterConfig,
            provider.GetRequiredService<IDomainToolRegistry>(),
            metricsPublisher: provider.GetService<IMetricsPublisher>(),
            loggerFactory: provider.GetService<ILoggerFactory>(),
            chatClientFactory: (_, _, _) => new StubChatClient());

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
            var thread = await agent.CreateSessionAsync(cts.Token);
            await foreach (var _ in agent.RunStreamingAsync("hi", thread, cancellationToken: cts.Token))
            {
                // consume the streamed updates so the agent reaches the natural end of run
            }
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

file sealed class StubChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ""))
        {
            FinishReason = ChatFinishReason.Stop
        });

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "")
        {
            FinishReason = ChatFinishReason.Stop
        };
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
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
