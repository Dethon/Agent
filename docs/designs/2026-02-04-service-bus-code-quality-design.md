# Service Bus Code Quality Improvements

## Overview

Refactor the Azure Service Bus messaging implementation to improve extensibility, understandability, and maintainability. Focus on single-responsibility components and explicit routing with minimal side effects.

## Current Pain Points

1. **ServiceBusChatMessengerClient** does too much: prompt enqueueing, response accumulation, sourceId mapping, and state management
2. **CompositeChatMessengerClient** has embedded routing logic in `GetClientsForSource()`
3. **ServiceBusProcessorHost** has inline message parsing that's hard to test
4. **ServiceBusResponseWriter** swallows exceptions silently without retry

## Proposed Architecture

### Component Breakdown

```
Infrastructure/Clients/Messaging/
├── ServiceBus/
│   ├── ServiceBusChatMessengerClient.cs    # Thin facade delegating to components
│   ├── ServiceBusPromptReceiver.cs         # Handles incoming prompts from queue
│   ├── ServiceBusResponseHandler.cs        # Accumulates and sends responses
│   ├── ServiceBusMessageParser.cs          # Parses queue messages to domain types
│   └── ServiceBusResponseWriter.cs         # Sends responses with Polly retry
├── ServiceBusConversationMapper.cs         # Maps sourceId → chatId/threadId
├── MessageSourceRouter.cs                  # Routes messages to appropriate clients
└── CompositeChatMessengerClient.cs         # Uses router for delegation
```

### 1. Split ServiceBusChatMessengerClient

**Before:** Single class with 136 lines doing multiple things

**After:** Facade delegating to focused components

```csharp
// ServiceBusChatMessengerClient.cs - thin facade
public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler,
    string defaultAgentId) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => false;
    public MessageSource Source => MessageSource.ServiceBus;

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken ct)
        => promptReceiver.ReadPromptsAsync(ct);

    public Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken ct)
        => responseHandler.ProcessAsync(updates, ct);

    // Thread methods return no-op values (Service Bus doesn't manage threads)
    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken ct)
        => Task.FromResult(0);

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken ct)
        => Task.FromResult(false);

    public Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source, long? chatId, long? threadId, string? agentId, string? topicName, CancellationToken ct)
        => Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? defaultAgentId));

    public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct)
        => Task.CompletedTask;
}
```

```csharp
// ServiceBusPromptReceiver.cs - receives prompts from queue
public sealed class ServiceBusPromptReceiver(
    ServiceBusConversationMapper conversationMapper,
    ILogger<ServiceBusPromptReceiver> logger)
{
    private readonly Channel<ChatPrompt> _channel = Channel.CreateUnbounded<ChatPrompt>();
    private int _messageIdCounter;

    public IAsyncEnumerable<ChatPrompt> ReadPromptsAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public async Task EnqueueAsync(ParsedServiceBusMessage message, CancellationToken ct)
    {
        var (chatId, threadId, _, _) = await conversationMapper.GetOrCreateMappingAsync(
            message.SourceId, message.AgentId, ct);

        var prompt = new ChatPrompt
        {
            Prompt = message.Prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = Interlocked.Increment(ref _messageIdCounter),
            Sender = message.Sender,
            AgentId = message.AgentId,
            Source = MessageSource.ServiceBus
        };

        logger.LogInformation(
            "Enqueued prompt from Service Bus: sourceId={SourceId}, chatId={ChatId}",
            message.SourceId, chatId);

        await _channel.Writer.WriteAsync(prompt, ct);
    }

    // Expose for response handler to look up sourceId by chatId
    public bool TryGetSourceId(long chatId, out string sourceId)
        => conversationMapper.TryGetSourceId(chatId, out sourceId);
}
```

```csharp
// ServiceBusResponseHandler.cs - accumulates and sends responses
public sealed class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter,
    string defaultAgentId,
    ILogger<ServiceBusResponseHandler> logger)
{
    private readonly ConcurrentDictionary<long, StringBuilder> _accumulators = new();

    public async Task ProcessAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken ct)
    {
        await foreach (var (key, update, _, _) in updates.WithCancellation(ct))
        {
            if (!promptReceiver.TryGetSourceId(key.ChatId, out var sourceId))
                continue;

            await ProcessUpdateAsync(key, update, sourceId, ct);
        }
    }

    private async Task ProcessUpdateAsync(AgentKey key, AgentResponseUpdate update, string sourceId, CancellationToken ct)
    {
        var accumulator = _accumulators.GetOrAdd(key.ChatId, _ => new StringBuilder());

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    accumulator.Append(tc.Text);
                    break;

                case StreamCompleteContent when accumulator.Length > 0:
                    await responseWriter.WriteResponseAsync(
                        sourceId, key.AgentId ?? defaultAgentId, accumulator.ToString(), ct);
                    accumulator.Clear();
                    _accumulators.TryRemove(key.ChatId, out _);
                    break;
            }
        }
    }
}
```

### 2. Extract Message Parser

```csharp
// Domain/DTOs/ParsedServiceBusMessage.cs
public sealed record ParsedServiceBusMessage(
    string Prompt,
    string Sender,
    string SourceId,
    string AgentId);

// ParseResult.cs - captures success or failure with reason
public abstract record ParseResult;
public sealed record ParseSuccess(ParsedServiceBusMessage Message) : ParseResult;
public sealed record ParseFailure(string Reason, string Details) : ParseResult;
```

```csharp
// ServiceBusMessageParser.cs
public sealed class ServiceBusMessageParser(string defaultAgentId)
{
    public ParseResult Parse(ServiceBusReceivedMessage message)
    {
        string body;
        try
        {
            body = message.Body.ToString();
        }
        catch (Exception ex)
        {
            return new ParseFailure("BodyReadError", ex.Message);
        }

        ServiceBusPromptMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ServiceBusPromptMessage>(body);
        }
        catch (JsonException ex)
        {
            return new ParseFailure("DeserializationError", ex.Message);
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.Prompt))
        {
            return new ParseFailure("MalformedMessage", "Missing required 'prompt' field");
        }

        var sourceId = message.ApplicationProperties.TryGetValue("sourceId", out var sid)
            ? sid?.ToString() ?? GenerateSourceId()
            : GenerateSourceId();

        var agentId = message.ApplicationProperties.TryGetValue("agentId", out var aid)
            ? aid?.ToString() ?? defaultAgentId
            : defaultAgentId;

        return new ParseSuccess(new ParsedServiceBusMessage(
            parsed.Prompt,
            parsed.Sender,
            sourceId,
            agentId));
    }

    private static string GenerateSourceId() => Guid.NewGuid().ToString("N");
}
```

```csharp
// ServiceBusProcessorHost.cs - simplified
public sealed class ServiceBusProcessorHost(
    ServiceBusProcessor processor,
    ServiceBusMessageParser parser,
    ServiceBusPromptReceiver promptReceiver,
    ILogger<ServiceBusProcessorHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += ProcessErrorAsync;

        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var result = parser.Parse(args.Message);

        switch (result)
        {
            case ParseSuccess success:
                await promptReceiver.EnqueueAsync(success.Message, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message);
                break;

            case ParseFailure failure:
                logger.LogWarning("Failed to parse message: {Reason} - {Details}", failure.Reason, failure.Details);
                await args.DeadLetterMessageAsync(args.Message, failure.Reason, failure.Details);
                break;
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception,
            "Service Bus processor error: Source={ErrorSource}, Namespace={Namespace}, EntityPath={EntityPath}",
            args.ErrorSource, args.FullyQualifiedNamespace, args.EntityPath);
        return Task.CompletedTask;
    }
}
```

### 3. Extract Message Source Router

```csharp
// IMessageSourceRouter.cs (Domain/Contracts/)
public interface IMessageSourceRouter
{
    IEnumerable<IChatMessengerClient> GetClientsForSource(
        IReadOnlyList<IChatMessengerClient> clients,
        MessageSource source);
}

// MessageSourceRouter.cs (Infrastructure)
public sealed class MessageSourceRouter : IMessageSourceRouter
{
    public IEnumerable<IChatMessengerClient> GetClientsForSource(
        IReadOnlyList<IChatMessengerClient> clients,
        MessageSource source)
    {
        // WebUI client receives all messages (for streaming display)
        // Source-specific clients only receive their own messages
        return clients.Where(c => c.Source == MessageSource.WebUi || c.Source == source);
    }
}
```

```csharp
// CompositeChatMessengerClient.cs - uses router
public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients,
    IMessageSourceRouter router) : IChatMessengerClient
{
    // ... same logic but delegates to router.GetClientsForSource()
}
```

### 4. Add Polly Retry to ResponseWriter

```csharp
// ServiceBusResponseWriter.cs
public sealed class ServiceBusResponseWriter
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusResponseWriter> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;

    public ServiceBusResponseWriter(
        ServiceBusSender sender,
        ILogger<ServiceBusResponseWriter> logger)
    {
        _sender = sender;
        _logger = logger;

        _retryPolicy = Policy
            .Handle<ServiceBusException>(ex => ex.IsTransient)
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (ex, delay, attempt, _) =>
                {
                    _logger.LogWarning(ex,
                        "Retry {Attempt}/3 for Service Bus send after {Delay}s",
                        attempt, delay.TotalSeconds);
                });
    }

    public async Task WriteResponseAsync(
        string sourceId, string agentId, string response, CancellationToken ct = default)
    {
        var responseMessage = new ServiceBusResponseMessage
        {
            SourceId = sourceId,
            Response = response,
            AgentId = agentId,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(responseMessage);
        var message = new ServiceBusMessage(BinaryData.FromString(json))
        {
            ContentType = "application/json"
        };

        try
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                await _sender.SendMessageAsync(message, ct);
            });

            _logger.LogDebug("Sent response to queue for sourceId={SourceId}", sourceId);
        }
        catch (Exception ex)
        {
            // After all retries exhausted, log and don't block prompt processing
            _logger.LogError(ex,
                "Failed to send response to queue after retries for sourceId={SourceId}",
                sourceId);
        }
    }

    private sealed record ServiceBusResponseMessage
    {
        public required string SourceId { get; init; }
        public required string Response { get; init; }
        public required string AgentId { get; init; }
        public required DateTimeOffset CompletedAt { get; init; }
    }
}
```

### 5. Update ConversationMapper

Rename `ServiceBusSourceMapper` → `ServiceBusConversationMapper` and add reverse lookup:

```csharp
// ServiceBusConversationMapper.cs
public sealed class ServiceBusConversationMapper(
    IConnectionMultiplexer redis,
    IThreadStateStore threadStateStore,
    ILogger<ServiceBusConversationMapper> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly ConcurrentDictionary<long, string> _chatIdToSourceId = new();

    public async Task<(long ChatId, long ThreadId, string TopicId, bool IsNew)> GetOrCreateMappingAsync(
        string sourceId, string agentId, CancellationToken ct = default)
    {
        // ... existing logic ...

        // Track reverse mapping in memory for response routing
        _chatIdToSourceId[chatId] = sourceId;

        return (chatId, threadId, topicId, isNew);
    }

    public bool TryGetSourceId(long chatId, out string sourceId)
        => _chatIdToSourceId.TryGetValue(chatId, out sourceId!);
}
```

## File Changes Summary

| File | Action | Description |
|------|--------|-------------|
| `ServiceBusChatMessengerClient.cs` | Modify | Convert to thin facade |
| `ServiceBusPromptReceiver.cs` | New | Handles prompt enqueueing |
| `ServiceBusResponseHandler.cs` | New | Accumulates and sends responses |
| `ServiceBusMessageParser.cs` | New | Parses queue messages |
| `ServiceBusProcessorHost.cs` | Modify | Simplify using parser |
| `ServiceBusResponseWriter.cs` | Modify | Add Polly retry |
| `ServiceBusSourceMapper.cs` | Rename | → `ServiceBusConversationMapper.cs`, add reverse lookup |
| `MessageSourceRouter.cs` | New | Explicit routing logic |
| `IMessageSourceRouter.cs` | New | Router interface in Domain |
| `CompositeChatMessengerClient.cs` | Modify | Use injected router |
| `ParsedServiceBusMessage.cs` | New | DTO for parsed messages |
| `InjectorModule.cs` | Modify | Update DI registration |
| `Infrastructure.csproj` | Modify | Add Polly package reference |

## Dependencies

Add to `Infrastructure.csproj`:
```xml
<PackageReference Include="Microsoft.Extensions.Http.Polly" Version="9.*" />
```

## Testing Strategy

The new structure enables focused testing:

| Component | Test Focus |
|-----------|------------|
| `ServiceBusMessageParser` | Edge cases, malformed JSON, missing fields |
| `ServiceBusPromptReceiver` | Enqueueing, channel behavior |
| `ServiceBusResponseHandler` | Accumulation logic, stream completion |
| `ServiceBusConversationMapper` | Mapping persistence, reverse lookup |
| `MessageSourceRouter` | Routing rules per source |
| `ServiceBusResponseWriter` | Retry behavior (mock Polly or use test policy) |

## Migration Path

1. Add new components without removing old ones
2. Update DI to use new structure
3. Run integration tests to verify behavior unchanged
4. Remove any dead code

## Benefits

1. **Single Responsibility**: Each class has one reason to change
2. **Testability**: Components can be tested in isolation with clear boundaries
3. **Extensibility**: Adding new message sources means adding new parsers/receivers, not modifying existing code
4. **Understandability**: Smaller classes with focused responsibilities are easier to understand
5. **Resilience**: Polly retry handles transient failures gracefully
