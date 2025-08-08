using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Domain.Agents;

public class Agent
{
    private readonly IMcpClient[] _mcpClients;
    private readonly McpClientTool[] _mcpClientTools;
    private readonly McpClientResource[] _mcpClientResources;
    private readonly McpClientResourceTemplate[] _mcpClientResourceTemplates;
    private readonly Func<ChatMessage, CancellationToken, Task> _writeMessageCallback;
    private readonly ILargeLanguageModel _llm;
    private readonly List<ChatMessage> _messages = [];
    private readonly Lock _messagesLock = new();

    private Agent(
        IMcpClient[] mcpClients,
        McpClientTool[] mcpClientTools,
        McpClientResource[] mcpClientResources,
        McpClientResourceTemplate[] mcpClientResourceTemplates,
        Func<ChatMessage, CancellationToken, Task> writeMessageCallback,
        ILargeLanguageModel llm)
    {
        _mcpClients = mcpClients;
        _mcpClientTools = mcpClientTools;
        _mcpClientResources = mcpClientResources;
        _mcpClientResourceTemplates = mcpClientResourceTemplates;
        _writeMessageCallback = writeMessageCallback;
        _llm = llm;
    }

    public static async Task<Agent> CreateAsync(
        string[] endpoints, 
        Func<ChatMessage, CancellationToken, Task> writeMessageCallback,
        ILargeLanguageModel llm,
        CancellationToken ct)
    {
        var mcpClients = await Task.WhenAll(endpoints.Select(x => CreateClient(x, ct)));
        var tools = await GetTools(mcpClients, ct);
        var resources = await GetResources(mcpClients, ct).ToArrayAsync(ct);
        var templatedResources = await GetTemplatedResources(mcpClients, ct).ToArrayAsync(ct);
        
        var agent = new Agent(mcpClients, tools, resources, templatedResources, writeMessageCallback, llm);

        foreach (var client in mcpClients)
        {
            client.RegisterNotificationHandler(
                "notifications/resources/updated",
                async (notification, cancellationToken) =>
                {
                    var uri = notification.Params
                        .Deserialize<Dictionary<string, string>>()?
                        .GetValueOrDefault("uri");
                    if (uri is null)
                    {
                        return;
                    }
                    var resource = await client.ReadResourceAsync(uri, cancellationToken);
                    var message = new ChatMessage(ChatRole.User, resource.Contents.ToAIContents());
                    await agent.Run([message],cancellationToken);
                });
        }
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
    
    private static async IAsyncEnumerable<McpClientResource> GetResources(
        IMcpClient[] clients, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var client in clients)
        {
            if (client.ServerCapabilities.Resources is null)
            {
                continue;
            }
            var resources = client.EnumerateResourcesAsync(cancellationToken: cancellationToken);
            await foreach (var resource in resources)
            {
                await client.SubscribeToResourceAsync(resource.Uri, cancellationToken);
                yield return resource;
            }
        }
    }
    
    private static async IAsyncEnumerable<McpClientResourceTemplate> GetTemplatedResources(
        IMcpClient[] clients, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var client in clients)
        {
            if (client.ServerCapabilities.Resources is null)
            {
                continue;
            }
            var resources = client.EnumerateResourceTemplatesAsync(cancellationToken: cancellationToken);
            await foreach (var resource in resources)
            {
                await client.SubscribeToResourceAsync(resource.UriTemplate, cancellationToken);
                yield return resource;
            }
        }
    }
    
    public async Task Run(
        string? prompt, CancellationToken cancellationToken)
    {
        if (prompt is null)
        {
            await Run([], cancellationToken);
        }

        var message = new ChatMessage(ChatRole.User, prompt);
        await Run([message], cancellationToken);
    }

    public async Task Run(
        ChatMessage[] prompts, CancellationToken cancellationToken)
    {
        UpdateConversation(prompts);
        await ExecuteAgentLoop(0.5f, cancellationToken);
    }

    private async Task ExecuteAgentLoop(
        float? temperature = null,
        CancellationToken cancellationToken = default)
    {
        var updates = _llm.Prompt(GetConversationSnapshot(), _mcpClientTools, temperature, cancellationToken);
        await foreach (var update in updates)
        {
            if (update.Role is null)
            {
                continue;
            }

            var message = new ChatMessage(update.Role!.Value, update.Contents);
            UpdateConversation([message]);
            await _writeMessageCallback(message, cancellationToken);
        }
    }
    
    /*private CancellationToken GetChildCancellationToken(bool cancelPrevious, CancellationToken cancellationToken)
    {
        if (cancelPrevious)
        {
            foreach (var tokenSource in _cancelTokenSources.Values)
            {
                tokenSource.Cancel();
                tokenSource.Dispose();
            }

            _cancelTokenSources.Clear();
        }

        if (_cancelTokenSources.TryGetValue(cancellationToken, out var value))
        {
            return value.Token;
        }

        var newSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancelTokenSources[cancellationToken] = newSource;
        return newSource.Token;
    }*/

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
}