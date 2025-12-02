using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.Mappers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Agents;

public sealed class McpAgent : IAgent
{
    private ImmutableList<McpClient> _mcpClients = [];
    private ImmutableList<AITool> _mcpClientTools = [];

    private ImmutableDictionary<McpClient, ImmutableHashSet<string>> _availableResources =
        new Dictionary<McpClient, ImmutableHashSet<string>>().ToImmutableDictionary();

    private CancellationTokenSource _cancellationTokenSource = new();
    private bool _isDisposed;

    private readonly Func<AiResponse, CancellationToken, Task> _writeMessageCallback;
    private readonly IChatClient _chatClient;
    private ChatClientAgent _agent = null!;
    private ChatClientAgentThread _thread = null!;

    private readonly ConcurrentDictionary<string, (ChatClientAgent Agent, ChatClientAgentThread Thread)>
        _coAgentConversations = [];

    public DateTime LastExecutionTime { get; private set; }

    private McpAgent(
        IChatClient chatClient,
        Func<AiResponse, CancellationToken, Task> writeMessageCallback)
    {
        _chatClient = chatClient;
        _writeMessageCallback = writeMessageCallback;
    }

    public static async Task<IAgent> CreateAsync(
        string[] endpoints,
        Func<AiResponse, CancellationToken, Task> writeMessageCallback,
        IChatClient chatClient,
        CancellationToken ct)
    {
        var agent = new McpAgent(chatClient, writeMessageCallback);
        await agent.LoadMcps(endpoints, ct);
        return agent;
    }

    private async Task LoadMcps(string[] endpoints, CancellationToken ct)
    {
        _mcpClients = (await CreateClients(endpoints, ct)).ToImmutableList();
        _mcpClientTools = (await GetTools(_mcpClients, ct)).ToImmutableList();
        var systemPrompt = await GetPrompts(_mcpClients, ct);
        _availableResources = (await GetResources(_mcpClients, ct))
            .ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableHashSet());
        await SubscribeToResources(ct);

        _agent = CreateAgent(systemPrompt, _mcpClientTools);
        _thread = (ChatClientAgentThread)_agent.GetNewThread();
    }

    public async Task Run(string[] prompts, CancellationToken ct)
    {
        var messages = prompts
            .Select(x => new ChatMessage(ChatRole.User, x))
            .ToArray();
        await Run(messages, ct);
    }

    public async Task Run(AiMessage[] prompts, CancellationToken ct)
    {
        var messages = prompts.Select(x => x.ToChatMessage()).ToArray();
        await Run(messages, ct);
    }

    private async Task Run(ChatMessage[] prompts, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(prompts));

        LastExecutionTime = DateTime.UtcNow;
        var jointCt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token).Token;
        await ExecuteAgentLoop(prompts, jointCt);
    }

    private async Task ExecuteAgentLoop(ChatMessage[] prompts, CancellationToken ct)
    {
        var options = GetDefaultAgentRunOptions();

        await foreach (var update in RunStreamingWithCallback(_agent, prompts, _thread, options, ct))
        {
            await _writeMessageCallback(update, ct);
        }
    }

    private static async IAsyncEnumerable<AiResponse> RunStreamingWithCallback(
        ChatClientAgent agent,
        IEnumerable<ChatMessage> messages,
        ChatClientAgentThread thread,
        ChatClientAgentRunOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Dictionary<string, List<AgentRunResponseUpdate>> updatesByMessage = [];

        await foreach (var update in agent.RunStreamingAsync(messages, thread, options, ct))
        {
            if (update.MessageId is not { } messageId)
            {
                continue;
            }

            if (!updatesByMessage.TryGetValue(messageId, out var messageUpdates))
            {
                messageUpdates = [];
                updatesByMessage[messageId] = messageUpdates;
            }

            messageUpdates.Add(update);

            // Message is complete when we receive UsageContent or a tool call
            var hasUsage = update.Contents.Any(c => c is UsageContent);
            var hasToolCall = update.Contents.Any(c => c is FunctionCallContent);
            if (hasUsage || hasToolCall)
            {
                yield return messageUpdates.ToAiResponse();
            }
        }
    }

    public void CancelCurrentExecution()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public async ValueTask DisposeAsync()
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

    private ChatClientAgent CreateAgent(string? systemPrompt, IEnumerable<AITool> tools)
    {
        var chatOptions = new ChatOptions
        {
            Tools = tools.ToList(),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_effort"] = "low"
            }
        };

        return _chatClient.CreateAIAgent(new ChatClientAgentOptions
        {
            Name = "McpAgent",
            Instructions = systemPrompt,
            ChatOptions = chatOptions
        });
    }

    private static ChatClientAgentRunOptions GetDefaultAgentRunOptions(CreateMessageRequestParams? parameters = null)
    {
        var chatOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_effort"] = "low"
            }
        };

        if (parameters is not null)
        {
            chatOptions.Temperature = parameters.Temperature;
            chatOptions.MaxOutputTokens = parameters.MaxTokens;
            chatOptions.StopSequences = parameters.StopSequences?.ToArray();
        }

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
        await Run([message], jointCt);
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
                ? (CreateAgent(parameters?.SystemPrompt, _mcpClientTools), (ChatClientAgentThread)_agent.GetNewThread())
                : _coAgentConversations.GetOrAdd(tracker, _ =>
                {
                    var agent = CreateAgent(parameters?.SystemPrompt, _mcpClientTools);
                    return (agent, (ChatClientAgentThread)agent.GetNewThread());
                });

            var includeContext = parameters?.IncludeContext ?? ContextInclusion.None;
            var tools = includeContext == ContextInclusion.None ? [] : _mcpClientTools;
            var coAgentWithTools = CreateAgent(parameters?.SystemPrompt, tools);

            var messages = parameters?.Messages
                .Select(x => new ChatMessage(
                    x.Role == Role.Assistant ? ChatRole.Assistant : ChatRole.User,
                    x.Content.ToAIContents()))
                .ToArray() ?? [];

            var options = GetDefaultAgentRunOptions(parameters);
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