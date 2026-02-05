# Technical Concerns

## Preview Dependencies

### Microsoft.Agents.AI (1.0.0-preview)
- **Risk**: API may change before stable release
- **Impact**: McpAgent, ChatClientAgent usage
- **Mitigation**: Abstraction via DisposableAgent base class

### ModelContextProtocol (0.7.0-preview.1)
- **Risk**: MCP specification and SDK evolving
- **Impact**: All MCP server implementations
- **Mitigation**: Wrapper pattern in McpClientManager

## Architectural Considerations

### Redis as Single Data Store
- **Current State**: All state stored in Redis (threads, schedules, memory vectors)
- **Risk**: Redis becomes single point of failure
- **Impact**: Complete system unavailability if Redis down
- **Consideration**: Add resilience, potential backup store

### Vector Search Performance
- **Current State**: HNSW index in Redis Stack
- **Risk**: Performance degrades with large memory counts
- **Impact**: Memory recall latency
- **Consideration**: Monitor index size, consider partitioning

### MCP Server Process Model
- **Current State**: Each MCP server is separate process
- **Risk**: Process management complexity
- **Impact**: Deployment, monitoring overhead
- **Consideration**: Container orchestration required

## Code Quality Notes

### Thread Safety
- `McpAgent` uses `SemaphoreSlim` for session management
- `ConcurrentDictionary` for thread sessions
- **Note**: Ensure all shared state properly synchronized

### Disposal Patterns
- `McpAgent` implements `IAsyncDisposable`
- `ThreadSession` manages multiple disposable resources
- **Note**: Verify disposal chains complete properly

### Error Handling
- MCP tools rely on global filter (`AddCallToolFilter`)
- **Note**: Individual tools should NOT add try/catch
- HTTP clients use Polly retry policies

## Security Considerations

### Tool Approval
- Whitelist patterns control auto-approval
- Non-whitelisted tools require user approval
- **Note**: Review whitelist patterns in production

### User Authorization
- Telegram: `allowedUserNames` configuration
- WebChat: User registration required
- **Note**: No authentication on ChatHub by default

### Secrets Management
- User secrets for development
- Environment variables for production
- **Note**: Never commit API keys

## Known Limitations

### Telegram
- 4000 character message limit (truncation applied)
- Forum topics required for thread support
- Single bot token per agent

### WebChat
- No offline support
- Reconnection requires active stream subscription
- Browser refresh loses local state

### CLI
- Terminal.Gui has rendering quirks
- Limited to single session

## Monitoring Gaps

### Missing Instrumentation
- No distributed tracing
- Limited metrics collection
- Log aggregation not configured

### Recommendations
- Add OpenTelemetry
- Configure structured logging
- Health check endpoints

## Technical Debt Candidates

### Infrastructure/CliGui
- Complex Terminal.Gui integration
- Consider simplification if CLI mode rarely used

### Multiple Messenger Clients
- Similar patterns across Telegram/WebChat/ServiceBus/CLI
- Potential for further abstraction

### Service Bus Correlation Mapping
- Redis-backed correlationId to chatId mapping (`sb-correlation:{agentId}:{correlationId}`)
- 30-day TTL on mappings
- In-memory cache (`ConcurrentDictionary`) for reverse lookups
- **Note**: Memory cache not persisted across restarts

### Test Coverage
- Integration tests require running services
- Some edge cases may lack coverage
