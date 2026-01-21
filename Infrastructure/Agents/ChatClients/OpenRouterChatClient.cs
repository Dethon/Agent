using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.Agents.ChatClients;

public sealed class OpenRouterChatClient : IChatClient
{
    private readonly IChatClient _client;
    private readonly HttpClient _httpClient;
    private readonly HttpClientPipelineTransport _transport;
    private readonly ConcurrentQueue<string> _reasoningQueue = new();

    public OpenRouterChatClient(string endpoint, string apiKey, string model)
    {
        _httpClient = CreateHttpClient(_reasoningQueue);
        _transport = new HttpClientPipelineTransport(_httpClient);
        _client = CreateClient(endpoint, apiKey, model, _transport);
    }

    private ChatClientMetadata Metadata => _client.GetService<ChatClientMetadata>() ?? new ChatClientMetadata();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var allUpdates = await GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToListAsync(cancellationToken);
        return allUpdates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in _client.GetStreamingResponseAsync(messages, options, ct))
        {
            AppendReasoningContent(update);
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? key = null)
    {
        return serviceType == typeof(ChatClientMetadata)
            ? Metadata
            : _client.GetService(serviceType, key);
    }

    public void Dispose()
    {
        _client.Dispose();
        _transport.Dispose();
        _httpClient.Dispose();
    }

    private void AppendReasoningContent(ChatResponseUpdate update)
    {
        var reasoning = DrainReasoningQueue();
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            update.Contents.Add(new TextReasoningContent(reasoning));
        }
    }

    private string DrainReasoningQueue()
    {
        if (_reasoningQueue.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        while (_reasoningQueue.TryDequeue(out var chunk))
        {
            sb.Append(chunk);
        }

        return sb.ToString();
    }

    private static IChatClient CreateClient(
        string endpoint, string apiKey, string model, HttpClientPipelineTransport transport)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = transport
        };

        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
    }

    private static HttpClient CreateHttpClient(ConcurrentQueue<string> reasoningQueue)
    {
        var handler = new ReasoningHandler(reasoningQueue)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            }
        };
        return new HttpClient(handler);
    }

    private sealed class ReasoningHandler(ConcurrentQueue<string> queue) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await OpenRouterHttpHelpers.FixEmptyAssistantContentWithToolCalls(request, cancellationToken);
            var response = await base.SendAsync(request, cancellationToken);

            if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                response.Content = OpenRouterHttpHelpers.WrapWithReasoningTee(response.Content, queue);
            }

            return response;
        }
    }
}