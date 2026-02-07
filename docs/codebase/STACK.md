# Technology Stack

## Languages & Frameworks

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 10.0 | Runtime platform |
| C# | 14 | Primary language |
| ASP.NET Core | 10.0 | Web API and SignalR |
| Blazor WebAssembly | 10.0 | WebChat frontend |

## Core Dependencies

### AI/LLM
- **Microsoft.Extensions.AI** (10.2.0) - Microsoft AI abstractions
- **Microsoft.Agents.AI** (1.0.0-preview) - Agent framework
- **ModelContextProtocol** (0.7.0-preview.1) - MCP client/server SDK

### Data Storage
- **StackExchange.Redis** (2.10.1) - Redis client
- **NRedisStack** (1.2.0) - Redis Stack with vector search

### Messaging
- **Telegram.Bot** (22.8.1) - Telegram Bot API
- **Azure.Messaging.ServiceBus** (7.20.1) - Azure Service Bus
- **SignalR** (built-in) - Real-time WebChat

### Browser Automation
- **Microsoft.Playwright** (1.58.0) - Web automation

### CLI/UI
- **Terminal.Gui** (1.19.0) - Terminal UI framework
- **Spectre.Console** (0.54.0) - Console formatting
- **System.CommandLine** (2.0.2) - CLI parsing

### HTTP & Resilience
- **Polly** (8.6.5) - Resilience patterns
- **Microsoft.Extensions.Http.Polly** (10.0.2) - HTTP resilience

### Utilities
- **FluentResults** (4.0.0) - Result pattern
- **NCrontab** (3.4.0) - Cron expression parsing
- **SmartReader** (0.11.0) - Article extraction

## Testing Stack

| Package | Version | Purpose |
|---------|---------|---------|
| xUnit | 2.9.3 | Test framework |
| Moq | 4.20.72 | Mocking |
| Shouldly | 4.3.0 | Assertions |
| Testcontainers | 4.10.0 | Container testing |
| WireMock.Net | 1.25.0 | HTTP mocking |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.2 | Integration testing |

## Build & Runtime

- **SDK**: Microsoft.NET.Sdk.Web / Microsoft.NET.Sdk
- **Docker**: Linux containers supported
- **User Secrets**: Development configuration

## External Services

| Service | SDK | Purpose |
|---------|-----|---------|
| OpenRouter | HTTP API | LLM provider |
| Redis Stack | NRedisStack | State & vector storage |
| Telegram | Telegram.Bot | Messaging interface |
| Azure Service Bus | Azure SDK | Message queue |
| qBittorrent | HTTP API | Download management |
| Jackett | HTTP API | Torrent search |
| Brave Search | HTTP API | Web search |
| Idealista | HTTP API | Real estate search |
