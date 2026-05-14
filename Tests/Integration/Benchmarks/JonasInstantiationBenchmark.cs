using System.Diagnostics;
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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
        var jonas = LoadJonasDefinition();
        var endpoints = mcpStackFixture.Endpoints.ToArray();
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));
        var userId = $"benchmark-user-{Guid.NewGuid()}";

        // Warmup — absorbs JIT, HttpClient pool init, TLS handshakes, Redis warm-up.
        for (var i = 0; i < WarmupIterations; i++)
        {
            await RunOneIterationAsync(jonas, endpoints, stateStore, userId);
        }

        var measured = new List<long>(MeasuredIterations);
        var stopwatch = new Stopwatch();
        for (var i = 0; i < MeasuredIterations; i++)
        {
            stopwatch.Restart();
            await RunOneIterationAsync(jonas, endpoints, stateStore, userId);
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

    private static async Task RunOneIterationAsync(
        AgentDefinition jonas,
        string[] endpoints,
        IThreadStateStore stateStore,
        string userId)
    {
        using var cts = new CancellationTokenSource(_iterationTimeout);
        var conversationId = $"benchmark:{Guid.NewGuid()}";

        using var chatClient = new StubChatClient();

        var agent = new McpAgent(
            endpoints,
            chatClient,
            $"{jonas.Name}-{conversationId}",
            jonas.Description ?? "",
            stateStore,
            userId,
            jonas.CustomInstructions,
            reasoningEffort: jonas.ReasoningEffort);

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

    private static AgentDefinition LoadJonasDefinition()
    {
        var settings = _configuration.Get<Agent.Settings.AgentSettings>()
            ?? throw new InvalidOperationException("Failed to bind AgentSettings from configuration");

        return settings.Agents.FirstOrDefault(a => a.Id.Equals(AgentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"agent '{AgentId}' not found in appsettings.json");
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
