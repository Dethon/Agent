using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Extensions;
using Infrastructure.Agents.Mappers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Agents;

public sealed class McpAgent : DisposableAgent
{
    private ImmutableList<McpClient> _mcpClients = [];
    private ImmutableList<AITool> _mcpClientTools = [];

    private ImmutableDictionary<McpClient, ImmutableHashSet<string>> _availableResources =
        new Dictionary<McpClient, ImmutableHashSet<string>>().ToImmutableDictionary();

    private bool _isDisposed;

    private readonly IChatClient _chatClient;
    private ChatClientAgent _innerAgent = null!;
    private AgentThread? _innerThread;

    private readonly ReaderWriterLockSlim _resourceSyncLock = new(LockRecursionPolicy.SupportsRecursion);

    private Channel<AgentRunResponseUpdate> _subscriptionChannel;
    private readonly ConcurrentDictionary<string, AgentThread> _coAgentConversations = [];

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
        await agent.LoadMcps(endpoints, name, description, ct);
        return agent;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _resourceSyncLock.Dispose();
        _subscriptionChannel.Writer.TryComplete();
        await UnSubscribeToResources(CancellationToken.None);
        foreach (var client in _mcpClients)
        {
            await client.DisposeAsync();
        }
    }

    public override AgentThread GetNewThread()
    {
        var thread = _innerAgent.GetNewThread();
        return thread;
    }

    public override AgentThread DeserializeThread(
        JsonElement serializedThread,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
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

        options ??= GetDefaultAgentRunOptions();
        _innerThread = thread ?? GetNewThread();
        var mainResponse = RunStreamingAsync(messages, _innerThread, options, cancellationToken);
        return (await mainResponse.ToArrayAsync(cancellationToken)).ToAgentRunResponse();
    }

    public override IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_subscriptionChannel.Reader.Completion.IsCompleted)
        {
            _subscriptionChannel = CreateSubscriptionChannel();
        }

        var mainResponses = RunStreamingPrivateAsync(messages, thread, options, cancellationToken);
        var notificationResponses = _subscriptionChannel.Reader.ReadAllAsync(cancellationToken);
        return mainResponses.Merge(notificationResponses, cancellationToken);
    }

    private async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingPrivateAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        options ??= GetDefaultAgentRunOptions();
        _innerThread = thread ?? GetNewThread();

        try
        {
            _resourceSyncLock.EnterReadLock();
            await foreach (var update in _innerAgent.RunStreamingAsync(messages, _innerThread, options, ct))
            {
                yield return update;
            }
        }
        finally
        {
            _resourceSyncLock.ExitReadLock();
        }

        await SyncResources(_mcpClients, ct);
    }

    private McpAgent(IChatClient chatClient)
    {
        _subscriptionChannel = CreateSubscriptionChannel();
        _chatClient = chatClient;
    }

    private static Channel<AgentRunResponseUpdate> CreateSubscriptionChannel()
    {
        var options = new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest };
        return Channel.CreateBounded<AgentRunResponseUpdate>(options);
    }

    private async Task LoadMcps(
        string[] endpoints, string name, string description, CancellationToken ct)
    {
        _mcpClients = (await CreateClients(name, description, endpoints, ct)).ToImmutableList();
        _mcpClientTools = (await GetTools(_mcpClients, ct)).ToImmutableList();
        var systemPrompt = await GetPrompts(_mcpClients, ct);
        await SyncResources(_mcpClients, ct);
        SubscribeToNotifications();

        _innerAgent = CreateInnerAgent(systemPrompt, name, description);
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
        var allTools = _mcpClientTools;
        var chatOptions = new ChatOptions
        {
            Tools = allTools,
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

    private async Task<McpClient[]> CreateClients(
        string name, string description, string[] endpoints, CancellationToken ct)
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
                    ClientInfo = new Implementation
                    {
                        Name = name,
                        Description = description,
                        Version = "1.0.0"
                    },
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

    private void SubscribeToNotifications()
    {
        foreach (var client in _mcpClients)
        {
            client.RegisterNotificationHandler(
                "notifications/resources/updated",
                (notification, cancellationToken) =>
                    UpdatedResourceNotificationHandler(client, notification, cancellationToken));

            client.RegisterNotificationHandler(
                "notifications/resources/list_changed",
                (_, cancellationToken) =>
                    SyncResources([client], cancellationToken));
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
        McpClient client, JsonRpcNotification notification, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var uri = notification.Params
            .Deserialize<Dictionary<string, string>>()?
            .GetValueOrDefault("uri");
        if (uri is null || _subscriptionChannel.Reader.Completion.IsCompleted)
        {
            return;
        }

        var resource = await client.ReadResourceAsync(uri, cancellationToken);
        var message = new ChatMessage(ChatRole.User, resource.Contents.ToAIContents());

        await foreach (var update in RunStreamingPrivateAsync([message], _innerThread, ct: cancellationToken))
        {
            _subscriptionChannel?.Writer.TryWrite(update);
        }
    }

    private async ValueTask SyncResources(
        IEnumerable<McpClient> clients, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        foreach (var client in clients)
        {
            var currentResources = await client.EnumerateResourcesAsync(cancellationToken)
                .Select(x => x.Uri)
                .ToArrayAsync(cancellationToken);
            var previousResources = _availableResources.GetValueOrDefault(client) ?? [];
            var newResources = currentResources.Except(previousResources);
            var removedResources = previousResources.Except(currentResources);

            foreach (var uri in newResources)
            {
                await client.SubscribeToResourceAsync(uri, cancellationToken);
            }

            foreach (var uri in removedResources)
            {
                await client.UnsubscribeFromResourceAsync(uri, cancellationToken);
            }

            _availableResources = _availableResources.SetItem(client, currentResources.ToImmutableHashSet());

            try
            {
                _resourceSyncLock.EnterWriteLock();
                if (currentResources.Length == 0)
                {
                    _subscriptionChannel.Writer.TryComplete();
                }
                else if (_subscriptionChannel.Reader.Completion.IsCompleted)
                {
                    _subscriptionChannel = CreateSubscriptionChannel();
                }
            }
            finally
            {
                _resourceSyncLock.ExitWriteLock();
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
            ObjectDisposedException.ThrowIf(_isDisposed, this);
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