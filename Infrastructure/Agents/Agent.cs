using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Extensions;
using Infrastructure.LLMAdapters;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Agents;

public class Agent : IAgent
{
    private readonly IMcpClient[] _mcpClients;
    private readonly McpClientTool[] _mcpClientTools;
    private readonly Func<AiPartialResponse, CancellationToken, Task> _writeMessageCallback;
    private readonly OpenAiClient _llm;
    private readonly ConversationHistory _messages;
    private CancellationTokenSource _cancellationTokenSource = new();
    
    private Agent(
        IMcpClient[] mcpClients,
        McpClientTool[] mcpClientTools,
        ChatMessage[] initialMessages,
        Func<AiPartialResponse, CancellationToken, Task> writeMessageCallback,
        OpenAiClient llm)
    {
        _mcpClients = mcpClients;
        _mcpClientTools = mcpClientTools;
        _messages = new ConversationHistory(initialMessages);
        _writeMessageCallback = writeMessageCallback;
        _llm = llm;
    }

    public static async Task<IAgent> CreateAsync(
        string[] endpoints,
        ChatMessage[] initialMessages,
        Func<AiPartialResponse, CancellationToken, Task> writeMessageCallback,
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

    public async Task Run(ChatMessage[] prompts, CancellationToken cancellationToken)
    {
        _messages.AddMessages(prompts);
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
        var updates = _llm.Prompt(_messages.GetSnapshot(), _mcpClientTools, temperature, ct);
        await foreach (var update in updates)
        {
            _messages.AddMessages(update);
            await _writeMessageCallback(MapUpdate(update), ct);
        }
        
    }

    private AiPartialResponse MapUpdate(ChatResponseUpdate update)
    {
        throw new NotImplementedException();
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