using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.Mappers;
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
    private readonly OpenAiClient _llm;
    private readonly PersistentConversationHistory _messages;
    private readonly ConcurrentDictionary<string, ConversationHistory> _coAgentConversations = [];

    private McpAgent(
        OpenAiClient llm,
        PersistentConversationHistory messages,
        Func<AiResponse, CancellationToken, Task> writeMessageCallback)
    {
        _llm = llm;
        _writeMessageCallback = writeMessageCallback;
        _messages = messages;
    }

    public static async Task<IAgent> CreateAsync(
        string[] endpoints,
        string conversationId,
        AiMessage[] defaultMessages,
        Func<AiResponse, CancellationToken, Task> writeMessageCallback,
        OpenAiClient llm,
        IConversationHistoryStore conversationStore,
        CancellationToken ct)
    {
        var messages = await PersistentConversationHistory.LoadOrCreateAsync(
            conversationId,
            defaultMessages.Select(x => x.ToChatMessage()),
            conversationStore,
            ct);
        var agent = new McpAgent(llm, messages, writeMessageCallback);
        await agent.LoadMcps(endpoints, ct);
        return agent;
    }

    private async Task LoadMcps(string[] endpoints, CancellationToken ct)
    {
        _mcpClients = (await CreateClients(endpoints, ct)).ToImmutableList();
        _mcpClientTools = (await GetTools(_mcpClients, ct)).ToImmutableList();
        _availableResources = (await GetResources(_mcpClients, ct))
            .ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableHashSet());
        await SubscribeToResources(ct);
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

        var jointCt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token).Token;
        await _messages.AddMessagesAsync(prompts, jointCt);
        await ExecuteAgentLoop(jointCt);
    }

    private async Task ExecuteAgentLoop(CancellationToken ct)
    {
        var options = GetDefaultChatOptions();
        var updates = _llm.Prompt(_messages.GetSnapshot(), options, ct);

        List<ChatResponseUpdate> processedUpdates = [];
        Dictionary<string, List<ChatResponseUpdate>> updatesLookup = [];
        await foreach (var update in updates)
        {
            processedUpdates.Add(update);
            if (update.MessageId is null)
            {
                continue;
            }

            updatesLookup.TryAdd(update.MessageId, []);
            updatesLookup[update.MessageId].Add(update);
            var messageUpdates = updatesLookup[update.MessageId];
            if (messageUpdates.IsFinished())
            {
                await _writeMessageCallback(messageUpdates.ToAiResponse(), ct);
            }
        }

        await _messages.AddMessagesAsync(processedUpdates.ToChatResponse(), ct);
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

    private ChatOptions GetDefaultChatOptions(CreateMessageRequestParams? parameters = null)
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_effort"] = "low"
            },
            AllowMultipleToolCalls = true,
            Tools = _mcpClientTools
        };
        if (parameters is null)
        {
            return options;
        }

        var includeContext = parameters.IncludeContext ?? ContextInclusion.None;
        options.Tools = includeContext == ContextInclusion.None ? null : _mcpClientTools;
        options.Temperature = parameters.Temperature;
        options.MaxOutputTokens = parameters.MaxTokens;
        options.StopSequences = parameters.StopSequences?.ToArray();
        return options;
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
            var conversation = tracker is null
                ? new ConversationHistory([])
                : _coAgentConversations.GetOrAdd(tracker, _ => new ConversationHistory([]));
            conversation.AddMessages(parameters?.Messages);
            conversation.AddOrChangeSystemPrompt(parameters?.SystemPrompt);
            var options = GetDefaultChatOptions(parameters);

            var updates = _llm.Prompt(conversation.GetSnapshot(), options, ct);
            List<ChatResponseUpdate> processedUpdates = [];
            await foreach (var update in updates)
            {
                processedUpdates.Add(update);
                progress?.Report(new ProgressNotificationValue
                {
                    Progress = processedUpdates.Count
                });
            }

            var response = processedUpdates.ToChatResponse();
            conversation.AddMessages(response);
            return response.ToCreateMessageResult();
        };
    }
}