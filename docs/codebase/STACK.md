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

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.Extensions.AI | 10.2.0 | Microsoft AI abstractions and runtime |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | AI interface contracts (IChatClient, etc.) |
| Microsoft.Extensions.AI.OpenAI | 10.2.0-preview.1 | OpenAI-compatible client adapter |
| Microsoft.Agents.AI | 1.0.0-preview | Agent framework (ChatClientAgent, AgentSession) |
| Microsoft.Agents.AI.Abstractions | 1.0.0-preview | Agent abstractions (DisposableAgent, AgentResponse) |
| ModelContextProtocol | 0.8.0-preview.1 | MCP client SDK (tool discovery, sampling) |
| ModelContextProtocol.AspNetCore | 0.8.0-preview.1 | MCP server SDK (HTTP transport, tool/prompt registration) |

### Data Storage

| Package | Version | Purpose |
|---------|---------|---------|
| StackExchange.Redis | 2.10.14 | Redis client for state persistence |
| NRedisStack | 1.2.0 | Redis Stack with vector search (HNSW index) |

### Messaging

| Package | Version | Purpose |
|---------|---------|---------|
| Telegram.Bot | 22.8.1 | Telegram Bot API client |
| Azure.Messaging.ServiceBus | 7.20.1 | Azure Service Bus messaging |
| Microsoft.AspNetCore.SignalR.Client | 10.0.2 | SignalR client (tests, WebChat) |
| SignalR (built-in) | 10.0 | Real-time WebChat hub |

### Browser Automation

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.Playwright | 1.58.0 | Chromium-based web automation |

### WebChat Frontend

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.AspNetCore.Components.WebAssembly | 10.0.2 | Blazor WebAssembly runtime |
| Microsoft.AspNetCore.Components.WebAssembly.Server | 10.0.2 | Blazor WebAssembly server hosting |
| Markdig | 0.44.0 | Markdown-to-HTML rendering |
| System.Reactive | 6.1.0 | Reactive extensions for event streams |

### CLI/UI

| Package | Version | Purpose |
|---------|---------|---------|
| Terminal.Gui | 1.19.0 | Terminal UI framework |
| Spectre.Console | 0.54.0 | Console formatting and output |
| System.CommandLine | 2.0.2 | CLI argument parsing |

### HTTP & Resilience

| Package | Version | Purpose |
|---------|---------|---------|
| Polly | 8.6.5 | Resilience and transient fault handling |
| Polly.Extensions.Http | 3.0.0 | HTTP-specific Polly policies |
| Microsoft.Extensions.Http.Polly | 10.0.2 | HttpClient + Polly integration |

### Utilities

| Package | Version | Purpose |
|---------|---------|---------|
| FluentResults | 4.0.0 | Result pattern for error handling |
| NCrontab | 3.4.0 | Cron expression parsing for scheduling |
| SmartReader | 0.11.0 | Article content extraction from web pages |
| JetBrains.Annotations | 2025.2.4 | Code annotation attributes |
| Microsoft.Extensions.Caching.Memory | 10.0.2 | In-memory caching (Library, CommandRunner servers) |

## Testing Stack

| Package | Version | Purpose |
|---------|---------|---------|
| xUnit | 2.9.3 | Test framework |
| xunit.runner.visualstudio | 3.1.5 | VS test runner integration |
| Xunit.SkippableFact | 1.5.61 | Conditional test skipping |
| Moq | 4.20.72 | Mocking framework |
| Shouldly | 4.3.0 | Fluent assertions |
| Testcontainers | 4.10.0 | Docker-based integration testing |
| Testcontainers.ServiceBus | 4.10.0 | Service Bus container for integration tests |
| WireMock.Net | 1.25.0 | HTTP request/response mocking |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.2 | ASP.NET Core integration testing |
| Microsoft.NET.Test.Sdk | 18.0.1 | .NET test platform |
| coverlet.collector | 6.0.4 | Code coverage collection |

## Build & Runtime

- **SDKs**: `Microsoft.NET.Sdk.Web` (Agent, WebChat), `Microsoft.NET.Sdk.BlazorWebAssembly` (WebChat.Client), `Microsoft.NET.Sdk` (all others)
- **Docker**: Multi-stage Linux containers with NuGet cache mounts
- **Base Images**: `mcr.microsoft.com/dotnet/aspnet:10.0` (runtime), `mcr.microsoft.com/dotnet/sdk:10.0` (build)
- **User Secrets**: Development configuration via `Microsoft.Extensions.Configuration.UserSecrets`
- **Environment Variables**: Used for production configuration override
- **Service Worker**: WebChat.Client includes PWA service worker support
