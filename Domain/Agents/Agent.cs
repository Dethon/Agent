using System.Collections.Immutable;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Domain.Agents;

public class Agent : IAgent
{
    private readonly IMcpClient[] _mcpClients;
    private readonly McpClientTool[] _mcpClientTools;
    private readonly Func<ChatResponse, CancellationToken, Task> _writeMessageCallback;
    private readonly ILargeLanguageModel _llm;
    private readonly List<ChatMessage> _messages;
    private readonly Lock _messagesLock = new();
    private CancellationTokenSource _cancellationTokenSource = new();
    
    private Agent(
        IMcpClient[] mcpClients,
        McpClientTool[] mcpClientTools,
        ChatMessage[] initialMessages,
        Func<ChatResponse, CancellationToken, Task> writeMessageCallback,
        ILargeLanguageModel llm)
    {
        _mcpClients = mcpClients;
        _mcpClientTools = mcpClientTools;
        _messages = initialMessages.ToList();
        _writeMessageCallback = writeMessageCallback;
        _llm = llm;
    }

    public static async Task<IAgent> CreateAsync(
        string[] endpoints,
        ChatMessage[] initialMessages,
        Func<ChatResponse, CancellationToken, Task> writeMessageCallback,
        ILargeLanguageModel llm,
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
        for (var i = 0; i < 3; i++)
        {
            try
            {
                return await McpClientFactory.CreateAsync(
                    new SseClientTransport(
                        new SseClientTransportOptions
                        {
                            Endpoint = new Uri(endpoint)
                        }),
                    cancellationToken: cancellationToken);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }

        throw new HttpRequestException($"Failed to connect to MCP server at {endpoint} after 3 attempts.");
    }

    private static async Task<McpClientTool[]> GetTools(IMcpClient[] clients, CancellationToken cancellationToken)
    {
        var tasks = clients
            .Select(x => x
                .EnumerateToolsAsync(cancellationToken: cancellationToken)
                .ToArrayAsync(cancellationToken)
                .AsTask());
        var tools = await Task.WhenAll(tasks);
        return tools
            .SelectMany(x => x)
            .Select(x => x.WithProgress(new Progress<ProgressNotificationValue>()))
            .ToArray();
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

    public async Task Run(string? prompt, CancellationToken ct)
    {
        if (prompt is null)
        {
            await Run([], ct);
        }

        var message = new ChatMessage(ChatRole.User, prompt);
        await Run([message], ct);
    }

    public async Task Run(
        ChatMessage[] prompts, CancellationToken cancellationToken)
    {
        UpdateConversation(prompts);
        var jointCt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token).Token;
        await ExecuteAgentLoop(0.5f, jointCt);
    }

    public void CancelCurrentExecution(bool keepListening = false)
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    private async Task ExecuteAgentLoop(float? temperature, CancellationToken ct)
    {
        var updates = _llm.Prompt(GetConversationSnapshot(), _mcpClientTools, temperature, ct);
        var response = await updates.ToChatResponseAsync(ct);
        UpdateConversation(response);
        await _writeMessageCallback(response, ct);
    }
    
    private ImmutableArray<ChatMessage> GetConversationSnapshot()
    {
        lock (_messagesLock)
        {
            return [.._messages];
        }
    }
    
    private void UpdateConversation(IEnumerable<ChatMessage> messages)
    {
        lock (_messagesLock)
        {
            _messages.AddRange(messages);
        }
    }

    private void UpdateConversation(ChatResponse message)
    {
        lock (_messagesLock)
        {
            _messages.AddMessages(message);
        }
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
}