using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agent.Modules;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
using Infrastructure.Metrics;
using Microsoft.AspNetCore.SignalR.Client;
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
        var (provider, factory, _) = BuildServices(stubChannel: null);
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

            ReportSummary(measured);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProcessJonasMessageViaChatMonitor_FiveMeasuredIterations_CompletesUnderTimeout()
    {
        var stub = new StubChannelConnection();
        var (provider, _, monitor) = BuildServices(stub);
        using var monitorCts = new CancellationTokenSource();
        // ChatMonitor.Monitor is a long-running loop; let it run in the background while
        // we push messages into the stub channel and time the round-trip.
        var monitorTask = monitor!.Monitor(monitorCts.Token);

        try
        {
            var userId = $"benchmark-user-{Guid.NewGuid()}";

            for (var i = 0; i < WarmupIterations; i++)
            {
                await ProcessOneMessageAsync(stub, userId);
            }

            var measured = new List<long>(MeasuredIterations);
            var stopwatch = new Stopwatch();
            for (var i = 0; i < MeasuredIterations; i++)
            {
                stopwatch.Restart();
                await ProcessOneMessageAsync(stub, userId);
                stopwatch.Stop();
                measured.Add(stopwatch.ElapsedMilliseconds);
                output.WriteLine($"[iteration {i + 1}] {stopwatch.ElapsedMilliseconds} ms");
            }

            ReportSummary(measured);
        }
        finally
        {
            await monitorCts.CancelAsync();
            await monitorTask;
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProcessJonasMessageViaSignalR_FiveMeasuredIterations_CompletesUnderTimeout()
    {
        using var setupCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Agent-side connection: receives notifications/channel/message from the channel container.
        await using var channelConnection = new McpChannelConnection("signalr");
        await channelConnection.ConnectAsync(mcpStackFixture.SignalRChannelMcpEndpoint, setupCts.Token);

        var (provider, _, monitor) = BuildServices(channelConnection);
        using var monitorCts = new CancellationTokenSource();
        var monitorTask = monitor!.Monitor(monitorCts.Token);

        try
        {
            var userId = $"benchmark-user-{Guid.NewGuid()}";

            // Browser-side connection: drives the round-trip via the SignalR hub.
            await using var hub = new HubConnectionBuilder()
                .WithUrl(mcpStackFixture.SignalRHubUrl)
                .Build();
            await hub.StartAsync(setupCts.Token);
            await hub.InvokeAsync("RegisterUser", userId, setupCts.Token);

            for (var i = 0; i < WarmupIterations; i++)
            {
                await ProcessOneSignalRMessageAsync(hub);
            }

            var measured = new List<long>(MeasuredIterations);
            var stopwatch = new Stopwatch();
            for (var i = 0; i < MeasuredIterations; i++)
            {
                stopwatch.Restart();
                await ProcessOneSignalRMessageAsync(hub);
                stopwatch.Stop();
                measured.Add(stopwatch.ElapsedMilliseconds);
                output.WriteLine($"[iteration {i + 1}] {stopwatch.ElapsedMilliseconds} ms");
            }

            ReportSummary(measured);
        }
        finally
        {
            await monitorCts.CancelAsync();
            await monitorTask;
            await provider.DisposeAsync();
        }
    }

    private (ServiceProvider Provider, IAgentFactory Factory, ChatMonitor? Monitor) BuildServices(
        IChannelConnection? stubChannel)
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

        ChatMonitor? monitor = null;
        if (stubChannel is not null)
        {
            monitor = new ChatMonitor(
                [stubChannel],
                factory,
                approvalHandlerFactory: (_, _) => new AutoApproveHandler(),
                provider.GetRequiredService<ChatThreadResolver>(),
                provider.GetRequiredService<IMetricsPublisher>(),
                memoryRecallHook: null,
                provider.GetRequiredService<ILogger<ChatMonitor>>());
        }

        return (provider, factory, monitor);
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

    private static async Task ProcessOneMessageAsync(StubChannelConnection stub, string userId)
    {
        using var cts = new CancellationTokenSource(_iterationTimeout);
        var conversationId = $"benchmark:{Guid.NewGuid()}";
        var completion = stub.AwaitCompletion(conversationId);

        stub.PushMessage(new ChannelMessage
        {
            ConversationId = conversationId,
            Content = "hi",
            Sender = userId,
            ChannelId = stub.ChannelId,
            AgentId = AgentId
        });

        await completion.WaitAsync(cts.Token);
    }

    private static async Task ProcessOneSignalRMessageAsync(HubConnection hub)
    {
        using var cts = new CancellationTokenSource(_iterationTimeout);

        // Unique topic + chat/thread ids per iteration so each round-trip targets a fresh
        // AgentKey, exercising MultiAgentFactory.Create end-to-end.
        var topicId = $"benchmark-topic-{Guid.NewGuid()}";
        var chatId = Random.Shared.NextInt64();
        var threadId = Random.Shared.NextInt64();

        var sessionStarted = await hub.InvokeAsync<bool>(
            "StartSession", AgentId, topicId, chatId, threadId, null, cts.Token);
        sessionStarted.ShouldBeTrue();

        var stream = hub.StreamAsync<ChatStreamMessage>(
            "SendMessage", topicId, "hi", null, cts.Token);

        await foreach (var _ in stream.WithCancellation(cts.Token))
        {
            // consume until the broadcast channel completes
        }
    }

    private void ReportSummary(IReadOnlyList<long> measured)
    {
        var min = measured.Min();
        var max = measured.Max();
        var mean = (long)measured.Average();
        output.WriteLine(
            $"[summary] iterations={MeasuredIterations} min={min} ms mean={mean} ms max={max} ms");

        measured.Count.ShouldBe(MeasuredIterations);
        measured.ShouldAllBe(t => t < _iterationTimeout.TotalMilliseconds);
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

internal sealed class StubChannelConnection : IChannelConnection
{
    private readonly Channel<ChannelMessage> _messages = Channel.CreateUnbounded<ChannelMessage>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _completions = new();

    public string ChannelId => "stub";

    public IAsyncEnumerable<ChannelMessage> Messages => _messages.Reader.ReadAllAsync();

    public void PushMessage(ChannelMessage message) => _messages.Writer.TryWrite(message);

    public Task AwaitCompletion(string conversationId)
        => GetOrAddCompletion(conversationId).Task;

    public Task SendReplyAsync(
        string conversationId, string content, ReplyContentType contentType,
        bool isComplete, string? messageId, CancellationToken ct)
    {
        if (isComplete)
        {
            GetOrAddCompletion(conversationId).TrySetResult();
        }
        return Task.CompletedTask;
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => Task.CompletedTask;

    public Task<string?> CreateConversationAsync(
        string agentId, string topicName, string sender, CancellationToken ct)
        => Task.FromResult<string?>(null);

    private TaskCompletionSource GetOrAddCompletion(string conversationId)
        => _completions.GetOrAdd(
            conversationId,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}