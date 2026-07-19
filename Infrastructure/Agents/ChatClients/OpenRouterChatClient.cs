using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
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
    private readonly int? _maxContextTokens;
    private readonly string _model;
    private readonly TimeProvider _timeProvider;

    public OpenRouterChatClient(
        string endpoint,
        string apiKey,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        string? sessionId = null,
        TimeProvider? timeProvider = null)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _metricsPublisher = metricsPublisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpClient = CreateHttpClient(_reasoningQueue, _costQueue, sessionId);
        _transport = new HttpClientPipelineTransport(_httpClient);
        _client = CreateClient(endpoint, apiKey, model, _transport);
    }

    internal OpenRouterChatClient(
        IChatClient innerClient,
        string model,
        int? maxContextTokens = null,
        IMetricsPublisher? metricsPublisher = null,
        TimeProvider? timeProvider = null)
    {
        _model = model;
        _maxContextTokens = maxContextTokens;
        _metricsPublisher = metricsPublisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var transformedMessages = messages.Select(x =>
        {
            var newMessage = x.Clone();
            var msgSender = newMessage.GetSenderId();
            var timestamp = newMessage.GetTimestamp();
            var location = newMessage.GetLocation();
            var satelliteId = newMessage.GetSatelliteId();
            var dismissedAlert = newMessage.GetDismissedAlert();
            if (newMessage.Role == ChatRole.User && (msgSender is not null || timestamp is not null || dismissedAlert is not null))
            {
                var hasLocation = !string.IsNullOrWhiteSpace(location);
                var hasSatellite = !string.IsNullOrWhiteSpace(satelliteId);
                var senderSegment = msgSender is null
                    ? null
                    : (hasLocation, hasSatellite) switch
                    {
                        (true, true) => $"Message from {msgSender} (in {location} via {satelliteId})",
                        (true, false) => $"Message from {msgSender} (in {location})",
                        (false, true) => $"Message from {msgSender} (via {satelliteId})",
                        (false, false) => $"Message from {msgSender}"
                    };

                var localTimestamp = timestamp is { } ts
                    ? TimeZoneInfo.ConvertTime(ts, _timeProvider.LocalTimeZone)
                    : (DateTimeOffset?)null;

                var prefix = (senderSegment, timestamp) switch
                {
                    (not null, not null) => $"[Current time: {localTimestamp:yyyy-MM-dd HH:mm:ss zzz}] {senderSegment}:\n",
                    (not null, null) => $"{senderSegment}:\n",
                    (null, not null) => $"[Current time: {localTimestamp:yyyy-MM-dd HH:mm:ss zzz}]:\n",
                    _ => ""
                };

                if (!string.IsNullOrWhiteSpace(dismissedAlert))
                {
                    prefix = $"[The user just dismissed the {dismissedAlert}]\n{prefix}";
                }

                newMessage.Contents = newMessage.Contents
                    .Prepend(new TextContent(prefix))
                    .ToList();
            }

            var memoryContext = newMessage.GetMemoryContext();
            if (memoryContext is not null && newMessage.Role == ChatRole.User)
            {
                var memoryBlock = FormatMemoryContext(memoryContext);
                newMessage.Contents = newMessage.Contents
                    .Prepend(new TextContent(memoryBlock))
                    .ToList();
            }

            return newMessage;
        }).ToList();

        var sender = transformedMessages
            .LastOrDefault(m => m.Role == ChatRole.User)
            ?.GetSenderId();

        var fixedOverhead = MessageTruncator.EstimateOptionsOverheadTokens(options);
        var truncated = MessageTruncator.Truncate(
            transformedMessages, _maxContextTokens,
            out var droppedCount, out var tokensBefore, out var tokensAfter,
            out var overflowDetected, fixedOverheadTokens: fixedOverhead);

        if (overflowDetected && _metricsPublisher is not null)
        {
            await _metricsPublisher.PublishAsync(new ContextTruncationEvent
            {
                Sender = sender ?? "unknown",
                Model = _model,
                DroppedMessages = droppedCount,
                EstimatedTokensBefore = tokensBefore,
                EstimatedTokensAfter = tokensAfter,
                MaxContextTokens = _maxContextTokens ?? 0
            }, ct);
        }

        UsageContent? usage = null;

        await foreach (var update in _client.GetStreamingResponseAsync(truncated, options, ct))
        {
            AppendReasoningContent(update);
            update.SetTimestamp(_timeProvider.GetUtcNow());

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

    private static string FormatMemoryContext(MemoryContext context)
    {
        var memoryLines = context.Memories
            .Select(r => $"- {r.Memory.Content} ({r.Memory.Category.ToString().ToLowerInvariant()}, importance: {r.Memory.Importance:F1})");

        var profileLine = context.Profile is not null
            ? [$"[User profile: {context.Profile.Summary}]"]
            : Enumerable.Empty<string>();

        var lines = new[] { "[Memory context]" }
            .Concat(memoryLines)
            .Concat(profileLine)
            .Append("[End memory context]");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    // One handler (= one connection pool) for the whole process: a per-conversation
    // handler would pay a fresh TCP+TLS handshake to OpenRouter on every new
    // conversation's first LLM call.
    private static readonly SocketsHttpHandler _sharedHandler = new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    internal static SocketsHttpHandler SharedHandler => _sharedHandler;

    private static HttpClient CreateHttpClient(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue, string? sessionId)
    {
        var handler = new ReasoningHandler(reasoningQueue, costQueue, sessionId) { InnerHandler = _sharedHandler };
        return new HttpClient(handler, disposeHandler: false);
    }

    private sealed class ReasoningHandler(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue, string? sessionId) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, sessionId, cancellationToken);
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