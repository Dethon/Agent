using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;

namespace Infrastructure.Agents;

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
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        CancellationToken ct)
    {
        var clients = await CreateClientsWithRetry(name, description, endpoints, handlers, ct);
        var tools = await LoadTools(clients, ct);
        var prompts = await LoadPrompts(clients, ct);
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

    private static async Task<McpClient[]> CreateClientsWithRetry(
        string name,
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

        var clients = await Task.WhenAll(endpoints.Select(endpoint =>
            retryPolicy.ExecuteAsync(() => McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
                new McpClientOptions
                {
                    ClientInfo = new Implementation { Name = name, Description = description, Version = "1.0.0" },
                    Handlers = handlers
                },
                cancellationToken: ct))));

        return clients;
    }

    private static async Task<AITool[]> LoadTools(IEnumerable<McpClient> clients, CancellationToken ct)
    {
        var tasks = clients.Select(c => c.ListToolsAsync(cancellationToken: ct).AsTask());
        var results = await Task.WhenAll(tasks);
        return results
            .SelectMany(t => t)
            .Select(t => t.WithProgress(new Progress<ProgressNotificationValue>()))
            .ToArray<AITool>();
    }

    private static async Task<string[]> LoadPrompts(IEnumerable<McpClient> clients, CancellationToken ct)
    {
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
            .ToArrayAsync(ct);
    }
}