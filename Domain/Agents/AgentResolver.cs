using Domain.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Domain.Agents;

public class AgentResolver(
    DownloaderPrompt downloaderPrompt,
    ILargeLanguageModel languageModel,
    string[] mcpServerEndpoints,
    IMemoryCache cache,
    ILoggerFactory loggerFactory) : IAgentResolver
{
    private const int MaxDepth = 20;

    public async Task<IAgent> Resolve(AgentType agentType, int? threadId = null)
    {
        if (threadId is null)
        {
            return await AgentFactory(agentType);
        }

        var agent = await GetAgentFromCache(agentType, threadId.Value, () => AgentFactory(agentType));
        if (agent is null)
        {
            throw new InvalidOperationException($"{agentType} for thread {threadId} found in cache but was null.");
        }

        return agent;
    }

    private async Task<IAgent> AgentFactory(AgentType agentType)
    {
        var tools = await GetMcpServers(mcpServerEndpoints);
        return agentType switch
        {
            AgentType.Download => new Agent(
                messages: await downloaderPrompt.Get(null),
                llm: languageModel,
                tools: tools,
                maxDepth: MaxDepth,
                logger: loggerFactory.CreateLogger<Agent>()),
            _ => throw new ArgumentException($"Unknown agent type: {agentType}")
        };
    }

    private async Task<IAgent?> GetAgentFromCache(AgentType agentType, int threadId, Func<Task<IAgent>> createAgent)
    {
        return await cache.GetOrCreateAsync($"IAgent-{agentType}-{threadId}", cacheEntry =>
        {
            cacheEntry.SetAbsoluteExpiration(DateTimeOffset.UtcNow.AddMonths(2));
            return createAgent();
        });
    }

    private static async Task<McpClientTool[]> GetMcpServers(string[] endpoints, CancellationToken cancellationToken = default)
    {
        var clients = await Task.WhenAll(endpoints.Select(x => McpClientFactory.CreateAsync(
            new SseClientTransport(
                new SseClientTransportOptions
                {
                    Endpoint = new Uri(x)
                }), cancellationToken: cancellationToken)));
        
        
        
        var tools = await Task.WhenAll(
            clients.Select(x => x.ListToolsAsync(cancellationToken: cancellationToken).AsTask()));
        return tools
            .SelectMany(x => x)
            .Select(x => x.WithProgress(new Progress<ProgressNotificationValue>()))
            .ToArray();
    }
}