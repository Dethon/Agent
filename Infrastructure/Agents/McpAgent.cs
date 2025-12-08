using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Agents;

public sealed class McpAgent : DisposableAgent
{
    private readonly IChatClient _chatClient;
    private readonly McpClientManager _clientManager;
    private readonly McpResourceManager _resourceManager;
    private readonly McpSamplingHandler _samplingHandler;

    private ChatClientAgent _innerAgent = null!;
    private AgentThread? _innerThread;
    private bool _isDisposed;

    public override string? Name => _innerAgent.Name;
    public override string? Description => _innerAgent.Description;

    public static async Task<DisposableAgent> CreateAsync(
        string[] endpoints,
        IChatClient chatClient,
        string name,
        string description,
        CancellationToken ct)
    {
        var agent = new McpAgent(chatClient);
        await agent.InitializeAsync(endpoints, name, description, ct);
        return agent;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _samplingHandler.Dispose();
        await _resourceManager.DisposeAsync();
        await _clientManager.DisposeAsync();
    }

    public override AgentThread GetNewThread()
    {
        return _innerAgent.GetNewThread();
    }

    public override AgentThread DeserializeThread(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return _innerAgent.DeserializeThread(serializedThread, jsonSerializerOptions);
    }

    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        options ??= CreateRunOptions();
        _innerThread = thread ?? GetNewThread();
        var response = RunStreamingAsync(messages, _innerThread, options, cancellationToken);
        return (await response.ToArrayAsync(cancellationToken)).ToAgentRunResponse();
    }

    public override IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _resourceManager.EnsureChannelActive();
        var mainResponses = RunStreamingCoreAsync(messages, thread, options, cancellationToken);
        var notificationResponses = _resourceManager.SubscriptionChannel.Reader.ReadAllAsync(cancellationToken);
        return mainResponses.Merge(notificationResponses, cancellationToken);
    }

    private McpAgent(IChatClient chatClient)
    {
        _chatClient = chatClient;
        _samplingHandler = new McpSamplingHandler(
            GetNewThread,
            systemPrompt => CreateInnerAgent(systemPrompt),
            CreateRunOptions);
        _clientManager = new McpClientManager(new McpClientHandlers
        {
            SamplingHandler = _samplingHandler.HandleAsync
        });
        _resourceManager = new McpResourceManager(RunStreamingForResource);
    }

    private async Task InitializeAsync(
        string[] endpoints,
        string name,
        string description,
        CancellationToken ct)
    {
        await _clientManager.InitializeAsync(name, description, endpoints, ct);
        var systemPrompt = await _clientManager.GetPromptsAsync(ct);
        await _resourceManager.SyncResourcesAsync(_clientManager.Clients, ct);
        _resourceManager.SubscribeToNotifications(_clientManager.Clients);
        _innerAgent = CreateInnerAgent(systemPrompt, name, description);
    }

    private async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        options ??= CreateRunOptions();
        _innerThread = thread ?? GetNewThread();

        await foreach (var update in _innerAgent.RunStreamingAsync(messages, _innerThread, options, ct))
        {
            yield return update;
        }

        await _resourceManager.SyncResourcesAsync(_clientManager.Clients, ct);
    }

    private IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingForResource(
        IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var options = CreateRunOptions();
        return _innerAgent.RunStreamingAsync(messages, _innerThread, options, ct);
    }

    private ChatClientAgent CreateInnerAgent(string? systemPrompt, string? name = null, string? description = null)
    {
        var chatOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoning_effort"] = "low" }
        };

        return _chatClient.CreateAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            Instructions = systemPrompt,
            ChatOptions = chatOptions,
            Description = description,
            ChatMessageStoreFactory = CreateConcurrentMessageStore
        });
    }

    private static ConcurrentChatMessageStore CreateConcurrentMessageStore(
        ChatClientAgentOptions.ChatMessageStoreFactoryContext context)
    {
        return context.SerializedState.ValueKind is JsonValueKind.Object
            ? new ConcurrentChatMessageStore(context.SerializedState, context.JsonSerializerOptions)
            : new ConcurrentChatMessageStore();
    }

    private ChatClientAgentRunOptions CreateRunOptions(CreateMessageRequestParams? parameters = null)
    {
        var chatOptions = new ChatOptions
        {
            Tools = [.. _clientManager.Tools],
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoning_effort"] = "low" }
        };

        if (parameters is null)
        {
            return new ChatClientAgentRunOptions(chatOptions);
        }

        chatOptions.Temperature = parameters.Temperature;
        chatOptions.MaxOutputTokens = parameters.MaxTokens;
        chatOptions.StopSequences = parameters.StopSequences?.ToArray();

        return new ChatClientAgentRunOptions(chatOptions);
    }
}