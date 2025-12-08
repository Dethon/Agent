using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Polly;

namespace Infrastructure.Agents;

internal sealed class McpClientManager(McpClientHandlers handlers) : IAsyncDisposable
{
    private ImmutableList<McpClient> _clients = [];
    private ImmutableList<AITool> _tools = [];
    private bool _isDisposed;

    public IReadOnlyList<McpClient> Clients => _clients;
    public IReadOnlyList<AITool> Tools => _tools;

    public async Task InitializeAsync(
        string name,
        string description,
        string[] endpoints,
        CancellationToken ct)
    {
        _clients = (await CreateClients(name, description, endpoints, ct)).ToImmutableList();
        _tools = (await GetTools(_clients, ct)).ToImmutableList();
    }

    public async Task<string?> GetPromptsAsync(CancellationToken ct)
    {
        var tasks = _clients
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

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }
    }

    private async Task<McpClient[]> CreateClients(
        string name,
        string description,
        string[] endpoints,
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
        var tasks = clients
            .Select(x => x.EnumerateToolsAsync(cancellationToken: ct).ToArrayAsync(ct).AsTask());
        var tools = await Task.WhenAll(tasks);
        return tools
            .SelectMany(x => x)
            .Select(x => x.WithProgress(new Progress<ProgressNotificationValue>()));
    }
}