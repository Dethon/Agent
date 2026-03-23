using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Infrastructure.Agents.ChatClients;

public sealed class OpenRouterChatClient : IChatClient
{
    private readonly IChatClient _client;
    private readonly HttpClient? _httpClient;
    private readonly HttpClientPipelineTransport? _transport;
    private readonly ConcurrentQueue<string> _reasoningQueue = new();
    private readonly ConcurrentQueue<decimal> _costQueue = new();
    private readonly IMetricsPublisher? _metricsPublisher;
    private readonly string _model;

    public OpenRouterChatClient(string endpoint, string apiKey, string model, IMetricsPublisher? metricsPublisher = null)
    {
        _model = model;
        _metricsPublisher = metricsPublisher;
        _httpClient = CreateHttpClient(_reasoningQueue, _costQueue);
        _transport = new HttpClientPipelineTransport(_httpClient);
        _client = CreateClient(endpoint, apiKey, model, _transport);
    }

    internal OpenRouterChatClient(IChatClient innerClient, string model, IMetricsPublisher? metricsPublisher = null)
    {
        _model = model;
        _metricsPublisher = metricsPublisher;
        _client = innerClient;
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
        var materializedMessages = messages.ToList();

        var sender = materializedMessages
            .LastOrDefault(m => m.Role == ChatRole.User)
            ?.GetSenderId();

        var transformedMessages = materializedMessages.Select(x =>
        {
            var newMessage = x.Clone();
            var msgSender = newMessage.GetSenderId();
            var timestamp = newMessage.GetTimestamp();
            if (newMessage.Role == ChatRole.User && (msgSender is not null || timestamp is not null))
            {
                var prefix = (msgSender, timestamp) switch
                {
                    (not null, not null) => $"[{timestamp:yyyy-MM-dd HH:mm:ss zzz}] Message from {msgSender}:\n",
                    (not null, null) => $"Message from {msgSender}:\n",
                    (null, not null) => $"[{timestamp:yyyy-MM-dd HH:mm:ss zzz}]:\n",
                    _ => ""
                };
                newMessage.Contents = newMessage.Contents
                    .Prepend(new TextContent(prefix))
                    .ToList();
            }

            return newMessage;
        });

        UsageContent? usage = null;

        await foreach (var update in _client.GetStreamingResponseAsync(transformedMessages, options, ct))
        {
            AppendReasoningContent(update);
            update.SetTimestamp(DateTimeOffset.UtcNow);

            var updateUsage = update.Contents.OfType<UsageContent>().FirstOrDefault();
            if (updateUsage is not null)
            {
                usage = updateUsage;
            }

            yield return update;
        }

        if (_metricsPublisher is not null && usage?.Details is not null)
        {
            var cost = DrainCostQueue() ?? 0m;
            await _metricsPublisher.PublishAsync(new TokenUsageEvent
            {
                Sender = sender ?? "unknown",
                Model = _model,
                InputTokens = (int)(usage.Details.InputTokenCount ?? 0),
                OutputTokens = (int)(usage.Details.OutputTokenCount ?? 0),
                Cost = cost
            }, ct);
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
        _transport?.Dispose();
        _httpClient?.Dispose();
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

    internal decimal? DrainCostQueue()
    {
        decimal? last = null;
        while (_costQueue.TryDequeue(out var cost))
        {
            last = cost;
        }

        return last;
    }

    private static HttpClient CreateHttpClient(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue)
    {
        var handler = new ReasoningHandler(reasoningQueue, costQueue)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            }
        };
        return new HttpClient(handler);
    }

    private sealed class ReasoningHandler(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue) : DelegatingHandler
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
                response.Content = OpenRouterHttpHelpers.WrapWithReasoningTee(
                    response.Content, reasoningQueue, costQueue);
            }

            return response;
        }
    }
}