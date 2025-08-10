using System.Collections.Immutable;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Agents;

public sealed class Agent : IAgent
{
    public DateTime LastExecutionTime { get; private set; }
    private readonly ImmutableList<IMcpClient> _mcpClients;
    private readonly ImmutableList<AITool> _mcpClientTools;
    private readonly Func<AiResponse, CancellationToken, Task> _writeMessageCallback;
    private readonly OpenAiClient _llm;
    private readonly ConversationHistory _messages;
    private CancellationTokenSource _cancellationTokenSource = new();
    private bool _isDisposed;
    
    private Agent(
        IEnumerable<IMcpClient> mcpClients,
        IEnumerable<AITool> mcpClientTools,
        ChatMessage[] initialMessages,
        Func<AiResponse, CancellationToken, Task> writeMessageCallback,
        OpenAiClient llm)
    {
        _mcpClients = mcpClients.ToImmutableList();
        _mcpClientTools = mcpClientTools.ToImmutableList();
        _messages = new ConversationHistory(initialMessages);
        _writeMessageCallback = writeMessageCallback;
        _llm = llm;
    }

    public static async Task<IAgent> CreateAsync(
        string[] endpoints,
        ChatMessage[] initialMessages,
        Func<AiResponse, CancellationToken, Task> writeMessageCallback,
        OpenAiClient llm,
        CancellationToken ct)
    {
        var mcpClients = await Task.WhenAll(endpoints.Select(x => CreateClient(x, ct)));
        var tools = await GetTools(mcpClients, ct);

        var agent = new Agent(mcpClients, tools, initialMessages, writeMessageCallback, llm);

        await agent.SubscribeToResources(ct);
        return agent;
    }
    
    private static async Task<IMcpClient> CreateClient(string endpoint, CancellationToken cancellationToken)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        return await retryPolicy.ExecuteAsync(async () => await McpClientFactory.CreateAsync(
            new SseClientTransport(
                new SseClientTransportOptions
                {
                    Endpoint = new Uri(endpoint)
                }),
            cancellationToken: cancellationToken));
    }

    private static async Task<IEnumerable<McpClientTool>> GetTools(IMcpClient[] clients,
        CancellationToken cancellationToken)
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

    private async Task SubscribeToResources(CancellationToken ct)
    {
        foreach (var client in _mcpClients)
        {
            if (client.ServerCapabilities.Resources is null)
            {
                continue;
            }

            var resourceUris = client.EnumerateResourcesAsync(ct).Select(x => x.Uri);
            var templatedResourceUris = client.EnumerateResourceTemplatesAsync(ct).Select(x => x.UriTemplate);
            var uris = resourceUris.Concat(templatedResourceUris);
            await foreach (var uri in uris)
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
        foreach (var client in _mcpClients)
        {
            if (client.ServerCapabilities.Resources is null)
            {
                continue;
            }

            var resourceUris = client.EnumerateResourcesAsync(ct).Select(x => x.Uri);
            var templatedResourceUris = client.EnumerateResourceTemplatesAsync(ct).Select(x => x.UriTemplate);
            var uris = resourceUris.Concat(templatedResourceUris);
            await foreach (var uri in uris)
            {
                await client.UnsubscribeFromResourceAsync(uri, ct);
            }
        }
    }

    public async Task Run(string[] prompts, CancellationToken ct)
    {
        var messages = prompts
            .Select(x => new ChatMessage(ChatRole.User, x))
            .ToArray();
        await Run(messages, ct);
    }

    public async Task Run(Domain.DTOs.ChatMessage[] prompts, CancellationToken ct)
    {
        var messages = prompts.Select(x => x.ToChatMessage()).ToArray();
        await Run(messages, ct);
    }

    private async Task Run(ChatMessage[] prompts, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(prompts));

        LastExecutionTime = DateTime.UtcNow;
        _messages.AddMessages(prompts);
        var jointCt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token).Token;
        await ExecuteAgentLoop(0.5f, jointCt);
    }

    public void CancelCurrentExecution()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    private async Task ExecuteAgentLoop(float? temperature, CancellationToken ct)
    {
        var updates = _llm.Prompt(_messages.GetSnapshot(), _mcpClientTools, temperature, ct);

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

        _messages.AddMessages(processedUpdates.ToChatResponse());
    }

    private async ValueTask UpdatedResourceNotificationHandler(
        IMcpClient client, JsonRpcNotification notification, CancellationToken ct)
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
    }
}