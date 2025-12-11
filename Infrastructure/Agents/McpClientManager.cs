using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;

namespace Infrastructure.Agents;

internal sealed class McpClientManager : IAsyncDisposable
{
    private bool _isDisposed;

    public IReadOnlyList<McpClient> Clients { get; private set; } = [];
    public IReadOnlyList<AITool> Tools { get; private set; } = [];
    public IReadOnlyList<string> Prompts { get; private set; } = [];

    public async Task InitializeAsync(
        string name,
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        CancellationToken ct)
    {
        Clients = (await CreateClients(name, description, endpoints, handlers, ct)).ToImmutableList();
        Tools = (await GetTools(Clients, ct)).ToImmutableList();
        Prompts = await GetPromptsAsync(ct);
    }

    public async Task<string[]> GetPromptsAsync(CancellationToken ct)
    {
        return await Clients
            .Where(client => client.ServerCapabilities.Prompts is not null)
            .ToAsyncEnumerable()
            .SelectMany<McpClient, string>(async (client, _, c) =>
            {
                var prompts = await client.ListPromptsAsync(cancellationToken: c);
                return await Task.WhenAll(prompts.Select(async p =>
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

    private static async Task<McpClient[]> CreateClients(
        string name,
        string description,
        string[] endpoints,
        McpClientHandlers handlers,
        CancellationToken ct)
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        return await Task.WhenAll(endpoints.Select(endpoint =>
            retryPolicy.ExecuteAsync(async () => await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }),
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = name,
                        Description = description,
                        Version = "1.0.0"
                    },
                    Handlers = handlers
                },
                cancellationToken: ct))));
    }

    private static async Task<IEnumerable<AITool>> GetTools(
        IEnumerable<McpClient> clients,
        CancellationToken ct)
    {
        var tasks = clients.Select(x => x.ListToolsAsync(cancellationToken: ct).AsTask());
        var tools = await Task.WhenAll(tasks);
        return tools
            .SelectMany(x => x)
            .Select(x => x.WithProgress(new Progress<ProgressNotificationValue>()));
    }
}