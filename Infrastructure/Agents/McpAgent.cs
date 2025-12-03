using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
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

public sealed class McpAgent : CancellableAiAgent
{
    private ImmutableList<McpClient> _mcpClients = [];
    private ImmutableList<AITool> _mcpClientTools = [];

    private ImmutableDictionary<McpClient, ImmutableHashSet<string>> _availableResources =
        new Dictionary<McpClient, ImmutableHashSet<string>>().ToImmutableDictionary();

    private CancellationTokenSource _cancellationTokenSource = new();
    private bool _isDisposed;

    private readonly Func<AiResponse, CancellationToken, Task>? _writeMessageCallback;
    private readonly IChatClient _chatClient;
    private ChatClientAgent _innerAgent = null!;
    private ChatClientAgentThread _thread = null!;

    private readonly ConcurrentDictionary<string, (ChatClientAgent Agent, ChatClientAgentThread Thread)>
        _coAgentConversations = [];

    public override string? Name => _innerAgent.Name;
    public override string? Description => _innerAgent.Description;

    private McpAgent(
        IChatClient chatClient,
        Func<AiResponse, CancellationToken, Task>? writeMessageCallback)
    {
        _chatClient = chatClient;
        _writeMessageCallback = writeMessageCallback;
    }

    public static async Task<McpAgent> CreateAsync(
        string[] endpoints,
        Func<AiResponse, CancellationToken, Task> writeMessageCallback,
        IChatClient chatClient,
        string name,
        string description,
        CancellationToken ct)
    {
        var agent = new McpAgent(chatClient, writeMessageCallback);
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

    public override async Task<AgentRunResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token).Token;

        options ??= GetDefaultAgentRunOptions();
        var result = await _innerAgent.RunAsync(messages, thread ?? _thread, options, linkedCt);
        if (_writeMessageCallback is not null)
        {
            await _writeMessageCallback(result.ToAgentRunResponseUpdates().ToAiResponse(), linkedCt);
        }

        return result;
    }

    public override async IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token).Token;

        options ??= GetDefaultAgentRunOptions();
        thread ??= _thread;
        var updates = _innerAgent.RunStreamingAsync(messages, thread, options, linkedCt);
        await foreach (var (update, response) in updates.ToUpdateAiResponsePairs().WithCancellation(linkedCt))
        {
            yield return update;
            if (response is not null && _writeMessageCallback is not null)
            {
                await _writeMessageCallback(response, linkedCt);
            }
        }
    }

    public override void CancelCurrentExecution()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await _cancellationTokenSource.CancelAsync();
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
        if (uri is null)
        {
            return;
        }

        var jointCt = CancellationTokenSource
            .CreateLinkedTokenSource(ct, _cancellationTokenSource.Token).Token;
        var resource = await client.ReadResourceAsync(uri, ct);
        var message = new ChatMessage(ChatRole.User, resource.Contents.ToAIContents());

        await foreach (var _ in RunStreamingAsync([message], cancellationToken: jointCt))
        {
            // Process updates through RunStreamingAsync which handles callbacks
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
            var (_, coThread) = tracker is null
                ? (CreateInnerAgent(parameters?.SystemPrompt), (ChatClientAgentThread)_innerAgent.GetNewThread())
                : _coAgentConversations.GetOrAdd(tracker, _ =>
                {
                    var agent = CreateInnerAgent(parameters?.SystemPrompt);
                    return (agent, (ChatClientAgentThread)agent.GetNewThread());
                });

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