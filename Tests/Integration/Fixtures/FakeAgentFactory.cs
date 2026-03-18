using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;

namespace Tests.Integration.Fixtures;

public sealed class FakeAgentFactory : IAgentFactory
{
    private readonly ConcurrentQueue<QueuedResponse> _responseQueue = new();
    private readonly AgentRegistryOptions _registryOptions = new();
    private readonly MultiAgentFactory _inner;
    private const int ResponseDelayMs = 10;

    public FakeAgentFactory()
    {
        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(() => _registryOptions);

        _inner = new MultiAgentFactory(
            new Mock<IServiceProvider>().Object,
            optionsMonitor.Object,
            new OpenRouterConfig { ApiUrl = "http://fake", ApiKey = "fake" },
            new Mock<IDomainToolRegistry>().Object);
    }

    public void ConfigureAgents(params AgentDefinition[] agents)
    {
        _registryOptions.Agents = agents;
    }

    public void EnqueueResponses(params string[] responses)
    {
        foreach (var response in responses)
        {
            _responseQueue.Enqueue(new QueuedResponse { Content = response });
        }
    }

    public void EnqueueToolCall(string toolName, Dictionary<string, object?>? arguments = null)
    {
        _responseQueue.Enqueue(new QueuedResponse
        {
            ToolCall = new ToolCallInfo(toolName, arguments ?? new Dictionary<string, object?>())
        });
    }

    public void EnqueueReasoning(string reasoning)
    {
        _responseQueue.Enqueue(new QueuedResponse { Reasoning = reasoning });
    }

    public void EnqueueError(string errorMessage)
    {
        _responseQueue.Enqueue(new QueuedResponse { Error = errorMessage });
    }

    public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId)
    {
        var responses = new List<QueuedResponse>();
        while (_responseQueue.TryDequeue(out var response))
        {
            responses.Add(response);
        }

        return new FakeDisposableAgent(responses, ResponseDelayMs);
    }

    public IReadOnlyList<AgentInfo> GetAvailableAgents(string? userId = null)
        => _inner.GetAvailableAgents(userId);

    public AgentInfo RegisterCustomAgent(string userId, CustomAgentRegistration registration)
        => _inner.RegisterCustomAgent(userId, registration);

    public bool UnregisterCustomAgent(string userId, string agentId)
        => _inner.UnregisterCustomAgent(userId, agentId);

    private record QueuedResponse
    {
        public string? Content { get; init; }
        public string? Reasoning { get; init; }
        public ToolCallInfo? ToolCall { get; init; }
        public string? Error { get; init; }
    }

    private record ToolCallInfo(string Name, Dictionary<string, object?> Arguments);

    private sealed class FakeAgentThread : AgentSession;

    private sealed class FakeDisposableAgent(IReadOnlyList<QueuedResponse> responses, int responseDelayMs)
        : DisposableAgent
    {
        public override string Name => "FakeAgent";
        public override string Description => "A fake agent for testing";

        public override ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public override ValueTask DisposeThreadSessionAsync(AgentSession thread)
        {
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new FakeAgentThread());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, 
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedThread,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new FakeAgentThread());
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? thread = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse());
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? thread = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var messageId = Guid.NewGuid().ToString();

            foreach (var response in responses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (response.Error is not null)
                {
                    throw new InvalidOperationException(response.Error);
                }

                await Task.Delay(responseDelayMs, cancellationToken); // Simulate streaming delay

                if (response.Content is not null)
                {
                    yield return new AgentResponseUpdate
                    {
                        MessageId = messageId,
                        Contents = [new TextContent(response.Content)]
                    };
                }

                if (response.Reasoning is not null)
                {
                    yield return new AgentResponseUpdate
                    {
                        MessageId = messageId,
                        Contents = [new TextReasoningContent(response.Reasoning)]
                    };
                }

                if (response.ToolCall is not null)
                {
                    yield return new AgentResponseUpdate
                    {
                        MessageId = messageId,
                        Contents =
                        [
                            new FunctionCallContent(
                                Guid.NewGuid().ToString(),
                                response.ToolCall.Name,
                                response.ToolCall.Arguments)
                        ]
                    };
                }
            }

            // Final update with usage info to signal completion
            yield return new AgentResponseUpdate
            {
                MessageId = messageId,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20 })]
            };
        }
    }
}