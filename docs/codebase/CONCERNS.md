# Technical Concerns

## Preview Dependencies

### Microsoft.Agents.AI (1.0.0-preview.260205.1)
- **Risk**: API may change before stable release
- **Impact**: McpAgent, ChatClientAgent, ChatClientAgentRunOptions, ChatHistoryProvider (RedisChatMessageStore), AgentSession, AgentResponseUpdate usage throughout the agent system
- **Mitigation**: Abstraction via DisposableAgent base class and `IAgentFactory`

### Microsoft.Extensions.AI.OpenAI (10.2.0-preview.1.26063.2)
- **Risk**: Preview version tied to .NET 10 preview cycle
- **Impact**: OpenRouter LLM client integration
- **Mitigation**: Wrapped behind `IChatClient` abstraction

### ModelContextProtocol (0.8.0-preview.1)
- **Risk**: MCP specification and SDK evolving rapidly
- **Impact**: All six MCP server implementations plus McpClientManager, McpSamplingHandler, McpResourceManager, McpSubscriptionManager
- **Mitigation**: Wrapper pattern in McpClientManager; tools use base Domain classes with thin MCP wrappers

## Architectural Considerations

### Redis as Single Data Store
- **Current State**: All state stored in Redis (thread messages, topic metadata, schedules, memory vectors, Service Bus correlation mappings)
- **Risk**: Redis becomes single point of failure; no backup or failover configured
- **Impact**: Complete system unavailability if Redis is down
- **Note**: `RedisThreadStateStore` uses `_server.KeysAsync(pattern:)` for topic listing, which performs a SCAN across the keyspace -- may degrade with large numbers of topics

### Vector Search Performance
- **Current State**: HNSW index in Redis Stack with COSINE distance, 1536-dimension FLOAT32 vectors
- **Risk**: Performance degrades with large memory counts; `GetByUserIdAsync` uses `KeysAsync(pattern:)` to enumerate all keys matching `memory:{userId}:*`, then loads each hash individually
- **Impact**: Memory recall latency for users with many memories; N+1 Redis query pattern in `GetByUserIdAsync`
- **Consideration**: Monitor index size, batch hash retrieval, or consider partitioning

### MCP Server Process Model
- **Current State**: Each MCP server is a separate ASP.NET process (6 total: Library, Text, WebSearch, Memory, Idealista, CommandRunner)
- **Risk**: Process management complexity; each server independently connects to external services
- **Impact**: Deployment, monitoring, and resource overhead
- **Consideration**: Container orchestration required; currently each has its own `ConfigModule.cs` with independent error handling filter

### Chat Message Storage Growth
- **Current State**: `RedisChatMessageStore.InvokedAsync` appends all request and response messages to existing history and saves the full array each time
- **Risk**: Unbounded chat history growth per thread -- no message pruning or token-based truncation
- **Impact**: Large Redis values for long conversations; serialization/deserialization cost increases linearly
- **Note**: Messages have TTL via `RedisThreadStateStore` expiration, but within that window history can grow without bound

## Code Quality Notes

### Thread Safety
- `McpAgent` uses `SemaphoreSlim` (`_syncLock`) for thread session creation and disposal; `ConcurrentDictionary` for thread sessions
- `ChatThreadResolver` uses `Lock` + `ConcurrentDictionary` to manage thread contexts
- `WebChatStreamManager` uses six `ConcurrentDictionary` instances plus a `Lock` (`_streamLock`) for stream lifecycle
- `MessagePipeline` (WebChat client) uses `Lock` for finalization state
- `StreamingService` (WebChat client) uses `SemaphoreSlim` for stream creation
- **Note**: Some operations span multiple concurrent dictionaries without unified locking (e.g., `WebChatStreamManager.GetOrCreateStream` checks `_responseChannels` then writes to `_currentPrompts` and `_cancellationTokens`); race conditions are possible under high concurrency but mitigated by the fact that topic operations are per-user

### Disposal Patterns
- `McpAgent` implements `IAsyncDisposable` with `Interlocked.Exchange` guard on `_isDisposed`
- `ThreadSession` implements `IAsyncDisposable`, disposing both `McpResourceManager` and `McpClientManager`
- `PlaywrightWebBrowser` implements `IAsyncDisposable`, closing browser context, browser, and Playwright instance
- `WebChatStreamManager` implements `IDisposable`, completing all channels and cancelling all tokens
- `ChatThreadResolver` implements `IDisposable` with `Interlocked.CompareExchange` guard
- **Note**: `CliChatMessageRouter` replaces `_inputQueue` in `ResetInputQueue` with a new `BlockingCollection` and calls `CompleteAdding` on the old one, but the old queue reference could be accessed concurrently in `OnInputReceived`

### Error Handling
- MCP tools rely on global filter (`AddCallToolFilter`) in each server's `ConfigModule.cs` -- individual tools must NOT add try/catch
- HTTP clients use Polly retry policies (e.g., `ServiceBusResponseWriter` with 3 retries, exponential backoff)
- `SafeAwaitAsync` extension swallows exceptions after logging; used extensively in WebChat notification paths (at least 10+ call sites)
- `WithErrorHandling` extension on `IAsyncEnumerable<AgentResponseUpdate>` catches exceptions during streaming and converts them to `ErrorContent` updates
- `IgnoreCancellation` extension catches `OperationCanceledException` during enumeration and silently breaks
- **Note**: `ServiceBusResponseWriter.WriteResponseAsync` swallows all exceptions after retry exhaustion (logs but does not propagate); failed responses are silently lost

### Unbounded Channels
- `WebChatMessengerClient._promptChannel`: unbounded
- `ServiceBusPromptReceiver._channel`: unbounded
- `CompositeChatMessengerClient` creates unbounded channels per client for response broadcasting
- `BroadcastChannel<T>` creates unbounded subscriber channels
- `AsyncGrouping._channel` (in `GroupByStreaming`): unbounded
- **Risk**: Under sustained load, unbounded channels could consume significant memory if producers outpace consumers
- **Mitigation**: `IAsyncEnumerableExtensions.Merge` uses bounded channel (capacity 1000) with `BoundedChannelFullMode.Wait`, providing backpressure at the merge point

## Security Considerations

### Tool Approval
- Whitelist patterns control auto-approval (`ToolPatternMatcher.IsWhitelisted`)
- Non-whitelisted tools require user approval via Telegram, WebChat, CLI, or auto-approve handler
- `TelegramToolApprovalHandler._pendingApprovals` is a static `ConcurrentDictionary` shared across all instances
- **Note**: Review whitelist patterns in production; overly broad patterns could auto-approve dangerous tools

### Command Execution
- `McpServerCommandRunner` exposes a `RunCommand` tool that executes arbitrary shell commands
- `BaseCliRunner.Run` starts processes with `RedirectStandardOutput` but does not redirect `StandardError` -- error output from commands is lost
- **Risk**: If the command runner MCP server is misconfigured or exposed, it provides arbitrary code execution
- **Mitigation**: Tool approval system controls access; commands are sandboxed by the server's OS-level permissions

### File System Access
- `TextToolBase.ValidateAndResolvePath` restricts file access to the configured vault path
- `MoveTool` and `RemoveTool` validate paths are within the library root and reject `..` segments
- `RemoveTool` moves files to a trash folder (`~/.trash`) rather than permanently deleting
- `LocalFileSystemClient.MoveToTrash` constructs trash path with timestamp and GUID for uniqueness
- **Note**: Path validation uses `StringComparison.OrdinalIgnoreCase` on all platforms, which may behave unexpectedly on case-sensitive Linux filesystems

### User Authorization
- Telegram: `allowedUserNames` configuration restricts access
- WebChat: User registration via `ChatHub.RegisterUser` -- accepts any non-empty userId string
- **Risk**: No authentication on ChatHub by default; `RegisterUser` does not verify identity
- **Note**: `ChatHub.IsRegistered` only checks if `Context.Items` contains a "UserId" key; any connected client can register with any userId

### Secrets Management
- User secrets for development (each MCP server and the Agent project use `AddUserSecrets<Program>()`)
- API keys stored in settings classes (e.g., `McpSettings.ApiKey` for OpenRouter, Brave Search, CapSolver)
- OAuth credentials for Idealista (`apiKey`/`apiSecret`)
- qBittorrent password in settings
- **Note**: Never commit API keys; environment variables used for production

### Browser Automation
- `PlaywrightWebBrowser` launches Chromium with `--no-sandbox` flag
- Stealth script patches navigator properties, WebGL, and user agent data to evade bot detection
- CapSolver integration for CAPTCHA solving (DataDome CAPTCHAs)
- **Risk**: `--no-sandbox` reduces browser security isolation; stealth evasion may violate terms of service of scraped websites

## Known Limitations

### Telegram
- Message truncation applied for long responses (Telegram API limits)
- Forum topics required for thread support
- Single bot token per agent
- `TelegramToolApprovalHandler` uses static dictionary for pending approvals -- not persisted across restarts

### WebChat
- No offline support
- Reconnection requires active stream subscription
- Browser refresh loses local state (relies on server-side history reload)
- `WebChatSessionManager` is fully in-memory -- session mappings lost on server restart
- `WebChatStreamManager` maintains six `ConcurrentDictionary` instances all in-memory

### CLI
- Terminal.Gui integration (v1.19.0) has known rendering quirks
- Limited to single session
- `CliChatMessageRouter.ResetInputQueue` creates new `BlockingCollection` without thread-safe swap -- potential for items to be added to the old queue

### Service Bus
- `ServiceBusConversationMapper` reverse lookup (`_chatIdToCorrelationId`) is in-memory only; lost on restart, causing in-flight responses to be undeliverable
- Correlation mappings in Redis have 30-day TTL; long-idle conversations may lose their mapping
- `ServiceBusResponseHandler.ProcessAsync` sends every completed response fragment, not just the final aggregated response

## Monitoring Gaps

### Missing Instrumentation
- No distributed tracing (no OpenTelemetry)
- Limited metrics collection
- Log aggregation not configured
- No dead-letter monitoring or alerting for Service Bus failures
- No health check endpoints for MCP server processes

### Recommendations
- Add OpenTelemetry for distributed tracing across agent, MCP servers, and external services
- Configure structured logging with aggregation
- Health check endpoints for all server processes
- Monitor Redis memory usage and key counts
- Alert on Service Bus dead-letter queue depth

## Technical Debt Candidates

### Infrastructure/CliGui
- Complex Terminal.Gui integration with custom routing, rendering, and command handling
- `BlockingCollection<string>` replaced in `ResetInputQueue` without thread-safe disposal
- Consider simplification if CLI mode is rarely used

### Multiple Messenger Clients
- Similar patterns across Telegram, WebChat, ServiceBus, and CLI implementations
- `CompositeChatMessengerClient` adds complexity with channel-based response broadcasting and per-message source routing
- `IMessageSourceRouter` determines which clients receive which updates
- Potential for further abstraction of the `IChatMessengerClient` contract

### WebChatStreamManager State Proliferation
- Six `ConcurrentDictionary` instances (`_responseChannels`, `_cancellationTokens`, `_streamBuffers`, `_currentPrompts`, `_currentSenderIds`, `_pendingPromptCounts`) track per-topic stream state
- Cleanup requires removing from all dictionaries in `CleanupStreamState`
- Consider consolidating into a single per-topic state object

### Service Bus Error Handling
- `ServiceBusProcessorHost` dead-letters invalid messages (parse failures)
- `ServiceBusResponseWriter` swallows exceptions after retry exhaustion (logs but does not propagate)
- No dead-letter monitoring or alerting configured
- `ServiceBusPromptReceiver.EnqueueAsync` uses `SafeAwaitAsync` for WebUI notifications -- notification failures silently logged

### WebChat Pipeline State
- `MessagePipeline` maintains in-memory finalization state (`_finalizedByTopic`) and pending user messages (`_pendingUserMessages`)
- State lost on browser refresh (relies on server-side history reload and `LoadHistory` repopulation)
- `ClearTopic` must be called on topic deletion to prevent finalization state leak
- `_pendingUserMessages` dictionary grows unboundedly as correlationIds are never removed

### SubscriptionMonitor Efficiency
- `SubscriptionMonitor` polls all download subscriptions every 5 seconds
- `MonitorAllDownloads` fires individual `GetDownloadItem` calls per tracked download (TODO comment acknowledges this: "Check all downloads in a single call")
- No batching or change-based notification from download client

### RedisThreadStateStore Topic Lookup
- `GetTopicByChatIdAndThreadIdAsync` calls `GetAllTopicsAsync` (which scans and deserializes all topics for an agent) then filters with `FirstOrDefault`
- This is an O(N) scan when a direct key lookup would suffice, but the key includes `topicId` which is not known at call time
- Consider adding a secondary index mapping `(agentId, chatId, threadId)` to `topicId`

### RedisChatMessageStore Concurrency
- `InvokedAsync` reads existing messages, concatenates new ones, and writes back -- but only the final write is under `SemaphoreSlim`
- The read-then-write pattern is not atomic: concurrent invocations could read stale data before the lock, causing message loss
- The `SemaphoreSlim` lock only protects the `SetMessagesAsync` call, not the full read-modify-write cycle

### Test Coverage
- Integration tests require running Redis, MCP servers, and optionally Service Bus
- `BaseCliRunner.Run` does not redirect `StandardError`, limiting test observability for failed commands
- WebChat.Client state tests cover stores, effects, and pipeline but browser-level integration testing is limited
