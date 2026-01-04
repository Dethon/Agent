using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpClientManager : IAsyncDisposable
{
    public IReadOnlyList<McpClient> Clients { get; }
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyList<string> Prompts { get; }

    private bool _isDisposed;

    private McpClientManager(
        IReadOnlyList<McpClient> clients,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<string> prompts)
    {
        Clients = clients;
        Tools = tools;
        Prompts = prompts;
    }

    public static async Task<McpClientManager> CreateAsync(
        string name,
        string userId,
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        CancellationToken ct)
    {
        var clientsWithEndpoints = await CreateClientsWithRetry(name, description, endpoints, handlers, ct);
        var tools = await LoadTools(clientsWithEndpoints, ct);
        var prompts = await LoadPrompts(clientsWithEndpoints.Select(c => c.Client), userId, ct);
        var clients = clientsWithEndpoints.Select(c => c.Client).ToArray();
        return new McpClientManager(clients, tools, prompts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (var client in Clients)
        {
            await client.DisposeAsync();
        }
    }

    private static async Task<(McpClient Client, string ServerName)[]> CreateClientsWithRetry(
        string name,
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        var clients = await Task.WhenAll(endpoints.Select(async endpoint =>
        {
            var client = await retryPolicy.ExecuteAsync(() => McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = name, Description = description, Version = "1.0.0" },
                    Handlers = handlers
                },
                cancellationToken: ct));

            var serverName = ExtractServerName(endpoint);
            return (client, serverName);
        }));

        return clients;
    }

    private static async Task<AITool[]> LoadTools(
        IEnumerable<(McpClient Client, string ServerName)> clients,
        CancellationToken ct)
    {
        var tasks = clients.Select(async c =>
        {
            var tools = await c.Client.ListToolsAsync(cancellationToken: ct);
            return tools.Select(t => new QualifiedMcpTool(c.ServerName, t));
        });

        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(t => t)
            .Select(t => t.WithProgress(new Progress<ProgressNotificationValue>()))
            .ToArray<AITool>();
    }

    private static string ExtractServerName(string endpoint)
    {
        var uri = new Uri(endpoint);
        return uri.Host;
    }

    private static async Task<string[]> LoadPrompts(
        IEnumerable<McpClient> clients, string userId, CancellationToken ct)
    {
        var userContextPrompt =
            $"## User Context\n\nCurrent user ID: `{userId}`\n\nUse this userId for all user-scoped operations.";
        return await clients
            .Where(c => c.ServerCapabilities.Prompts is not null)
            .ToAsyncEnumerable()
            .SelectMany<McpClient, string>(async (client, _, c) =>
            {
                var list = await client.ListPromptsAsync(cancellationToken: c);
                return await Task.WhenAll(list.Select(async p =>
                {
                    var result = await client.GetPromptAsync(p.Name, cancellationToken: c);
                    return string.Join("\n", result.Messages
                        .Select(m => m.Content)
                        .OfType<TextContentBlock>()
                        .Select(t => t.Text));
                }));
            })
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Prepend(userContextPrompt)
            .ToArrayAsync(ct);
    }
}