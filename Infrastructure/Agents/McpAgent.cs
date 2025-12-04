using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Infrastructure.Agents.Mappers;
using Infrastructure.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Agents;

public sealed class McpAgent : CancellableAiAgent
{
    private ImmutableList<McpClient> _mcpClients = [];
    private ImmutableList<AITool> _mcpClientTools = [];

    private ImmutableDictionary<McpClient, ImmutableHashSet<string>> _availableResources =
        new Dictionary<McpClient, ImmutableHashSet<string>>().ToImmutableDictionary();

    private readonly ResettableCancellationTokenSource _cancellationTokenSource = new();
    private bool _isDisposed;
    private bool _isCancelled;

    private readonly IChatClient _chatClient;
    private ChatClientAgent _innerAgent = null!;
    private AgentThread _thread = null!;

    private readonly ConcurrentQueue<ChannelWriter<AgentRunResponseUpdate>> _subscriptionWriters = [];
    private readonly ConcurrentDictionary<string, AgentThread> _coAgentConversations = [];

    public override string? Name => _innerAgent.Name;
    public override string? Description => _innerAgent.Description;

    private McpAgent(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public static async Task<McpAgent> CreateAsync(
        string[] endpoints,
        IChatClient chatClient,
        string name,
        string description,
        CancellationToken ct)
    {
        var agent = new McpAgent(chatClient);
        await agent.LoadMcps(endpoints, name, description, ct);
        return agent;
    }

    private async Task LoadMcps(string[] endpoints, string name, string description, CancellationToken ct)
    {
        _mcpClients = (await CreateClients(endpoints, ct)).ToImmutableList();
        _mcpClientTools = (await GetTools(_mcpClients, ct)).ToImmutableList();
        var systemPrompt = await GetPrompts(_mcpClients, ct);
        _availableResources = (await GetResources(_mcpClients, ct))
            .ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableHashSet());
        await SubscribeToResources(ct);

        _innerAgent = CreateInnerAgent(systemPrompt, name, description);
        _thread = (ChatClientAgentThread)_innerAgent.GetNewThread();
    }

    public override AgentThread GetNewThread()
    {
        return _innerAgent.GetNewThread();
    }

    public override AgentThread DeserializeThread(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
    {
        return _innerAgent.DeserializeThread(serializedThread, jsonSerializerOptions);
    }

    public override Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _isCancelled = false;
        using var linkedCts = _cancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        options ??= GetDefaultAgentRunOptions();
        return _innerAgent.RunAsync(messages, thread ?? _thread, options, ct);
    }

    public override IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _isCancelled = false;
        using var linkedCts = _cancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linkedCts.Token;

        options ??= GetDefaultAgentRunOptions();
        thread ??= _thread;
        return _innerAgent.RunStreamingAsync(messages, thread, options, ct);
    }

    public override void CancelCurrentExecution()
    {
        _isCancelled = true;
        _cancellationTokenSource.CancelAndReset();
        while (_subscriptionWriters.TryDequeue(out var oldWriter))
        {
            oldWriter.Complete();
        }
    }

    public override ChannelReader<AgentRunResponseUpdate> Subscribe(bool switchSubscription)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var options = new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest };
        var subscription = Channel.CreateBounded<AgentRunResponseUpdate>(options);
        if (switchSubscription)
        {
            while (_subscriptionWriters.TryDequeue(out var oldWriter))
            {
                oldWriter.Complete();
            }
        }

        _subscriptionWriters.Enqueue(subscription.Writer);

        return subscription.Reader;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cancellationTokenSource.Dispose();
        await UnSubscribeToResources(CancellationToken.None);
        foreach (var client in _mcpClients)
        {
            await client.DisposeAsync();
        }
    }

    private ChatClientAgent CreateInnerAgent(string? systemPrompt, string? name = null, string? description = null)
    {
        var chatOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_effort"] = "low"
            }
        };

        return _chatClient.CreateAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            Instructions = systemPrompt,
            ChatOptions = chatOptions,
            Description = description
        });
    }

    private ChatClientAgentRunOptions GetDefaultAgentRunOptions(CreateMessageRequestParams? parameters = null)
    {
        var chatOptions = new ChatOptions
        {
            Tools = _mcpClientTools,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_effort"] = "low"
            }
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

    private async Task<McpClient[]> CreateClients(string[] endpoints, CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        return await Task.WhenAll(endpoints.Select(x =>
            retryPolicy.ExecuteAsync(async () => await McpClient.CreateAsync(
                new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(x)
                    }),
                new McpClientOptions
                {
                    Handlers = new McpClientHandlers
                    {
                        SamplingHandler = SamplingHandler()
                    }
                },
                cancellationToken: ct))));
    }

    private static async Task<IEnumerable<AITool>> GetTools(
        IEnumerable<McpClient> clients, CancellationToken cancellationToken)
    {
        var tasks = clients
            .Select(x => x
                .EnumerateToolsAsync(cancellationToken: cancellationToken)
                .ToArrayAsync(cancellationToken)
                .AsTask());
        var tools = await Task.WhenAll(tasks);
        return tools
            .SelectMany(x => x)
            .Select(x => x.WithProgress(new Progress<ProgressNotificationValue>()));
    }

    private static async Task<Dictionary<McpClient, HashSet<string>>> GetResources(
        IEnumerable<McpClient> clients, CancellationToken ct)
    {
        var tasks = clients
            .Where(client => client.ServerCapabilities.Resources is not null)
            .Select(async client =>
            {
                var resourceUris = client.EnumerateResourcesAsync(ct).Select(x => x.Uri);
                var templatedResourceUris = client.EnumerateResourceTemplatesAsync(ct).Select(x => x.UriTemplate);
                var uris = await resourceUris.Concat(templatedResourceUris).ToArrayAsync(ct);
                return (client, uris);
            });

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(x => x.client, x => x.uris.ToHashSet());
    }

    private static async Task<string?> GetPrompts(IEnumerable<McpClient> clients, CancellationToken ct)
    {
        var tasks = clients
            .Where(client => client.ServerCapabilities.Prompts is not null)
            .Select(async client =>
            {
                var prompts = await client.EnumeratePromptsAsync(ct).ToArrayAsync(ct);
                var promptContents = await Task.WhenAll(prompts.Select(async p =>
                {
                    var result = await client.GetPromptAsync(p.Name, cancellationToken: ct);
                    return string.Join("\n", result.Messages
                        .Select(m => m.Content)
                        .OfType<TextContentBlock>()
                        .Select(t => t.Text));
                }));
                return string.Join("\n\n", promptContents);
            });

        var results = await Task.WhenAll(tasks);
        var combined = string.Join("\n\n", results.Where(r => !string.IsNullOrEmpty(r)));
        return string.IsNullOrEmpty(combined) ? null : combined;
    }

    private async Task SubscribeToResources(CancellationToken ct)
    {
        foreach (var (client, uris) in _availableResources)
        {
            foreach (var uri in uris)
            {
                await client.SubscribeToResourceAsync(uri, ct);
            }

            client.RegisterNotificationHandler(
                "notifications/resources/updated",
                (notification, cancellationToken) =>
                    UpdatedResourceNotificationHandler(client, notification, cancellationToken));
        }
    }

    private async Task UnSubscribeToResources(CancellationToken ct)
    {
        foreach (var (client, uris) in _availableResources)
        {
            foreach (var uri in uris)
            {
                await client.UnsubscribeFromResourceAsync(uri, ct);
            }
        }
    }

    private async ValueTask UpdatedResourceNotificationHandler(
        McpClient client, JsonRpcNotification notification, CancellationToken ct)
    {
        var uri = notification.Params
            .Deserialize<Dictionary<string, string>>()?
            .GetValueOrDefault("uri");
        if (uri is null || _isCancelled)
        {
            return;
        }

        using var linkedCts = _cancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;
        var resource = await client.ReadResourceAsync(uri, linkedCt);
        var message = new ChatMessage(ChatRole.User, resource.Contents.ToAIContents());

        await foreach (var update in RunStreamingAsync([message], cancellationToken: linkedCt))
        {
            foreach (var writer in _subscriptionWriters)
            {
                writer.TryWrite(update);
            }
        }
    }

    private Func<
            CreateMessageRequestParams?,
            IProgress<ProgressNotificationValue>,
            CancellationToken,
            ValueTask<CreateMessageResult>>
        SamplingHandler()
    {
        return async (parameters, progress, ct) =>
        {
            var tracker = parameters?.Metadata?.GetProperty("tracker").GetString();
            var coThread = tracker is null
                ? GetNewThread()
                : _coAgentConversations.GetOrAdd(tracker, static (_, ctx) => ctx.GetNewThread(), this);

            var includeContext = parameters?.IncludeContext ?? ContextInclusion.None;
            var coAgentWithTools = CreateInnerAgent(parameters?.SystemPrompt);

            var messages = parameters?.Messages
                .Select(x => new ChatMessage(
                    x.Role == Role.Assistant ? ChatRole.Assistant : ChatRole.User,
                    x.Content.ToAIContents()))
                .ToArray() ?? [];

            var options = GetDefaultAgentRunOptions(parameters);
            options.ChatOptions?.Tools = includeContext == ContextInclusion.None
                ? []
                : options.ChatOptions?.Tools;

            var updates = coAgentWithTools.RunStreamingAsync(messages, coThread, options, ct);
            List<AgentRunResponseUpdate> processedUpdates = [];
            await foreach (var update in updates)
            {
                processedUpdates.Add(update);
                progress?.Report(new ProgressNotificationValue
                {
                    Progress = processedUpdates.Count
                });
            }

            return processedUpdates.ToCreateMessageResult();
        };
    }
}