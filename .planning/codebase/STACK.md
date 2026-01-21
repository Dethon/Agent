# Technology Stack

**Analysis Date:** 2026-01-19

## Languages

**Primary:**
- C# 14 (.NET 10) - All application code across Agent, Infrastructure, Domain, MCP servers, and WebChat

**Secondary:**
- JavaScript - Stealth scripts in Playwright browser automation (`Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`)
- HTML/CSS - Blazor WebAssembly UI (`WebChat.Client/`)

## Runtime

**Environment:**
- .NET 10.0 (Target Framework: `net10.0`)
- Docker containers on Linux (via `mcr.microsoft.com/dotnet/aspnet:10.0` base image)

**Package Manager:**
- NuGet (implicit via .csproj files)
- Lockfile: Not used (standard NuGet restore)

## Frameworks

**Core:**
- Microsoft.Agents.AI 1.0.0-preview.260108.1 - Agent framework for AI orchestration
- Microsoft.Extensions.AI 10.2.0 - LLM abstraction layer (IChatClient)
- Microsoft.Extensions.AI.OpenAI 10.2.0-preview - OpenAI-compatible client
- ModelContextProtocol 0.6.0-preview.1 / ModelContextProtocol.AspNetCore 0.5.0-preview.1 - MCP client/server
- ASP.NET Core 10 (Microsoft.NET.Sdk.Web) - Web hosting for Agent and WebChat

**UI:**
- Microsoft.AspNetCore.Components.WebAssembly 10.0.2 - Blazor WebAssembly client
- Microsoft.AspNetCore.SignalR.Client 10.0.2 - Real-time WebChat communication
- Spectre.Console 0.54.0 - Rich CLI output
- Terminal.Gui 1.19.0 - CLI modal dialogs

**Testing:**
- xUnit 2.9.3 - Test runner
- Moq 4.20.72 - Mocking framework
- Shouldly 4.3.0 - Assertion library
- WireMock.Net 1.24.0 - HTTP mocking
- Testcontainers 4.10.0 - Docker-based integration tests
- Microsoft.AspNetCore.Mvc.Testing 10.0.2 - ASP.NET Core integration testing

**Build/Dev:**
- Docker Compose - Multi-container orchestration (`DockerCompose/docker-compose.yml`)
- User Secrets - Local development secrets management

## Key Dependencies

**Critical:**
- Telegram.Bot 22.8.1 - Telegram messaging client
- StackExchange.Redis 2.10.1 - Redis connection management
- NRedisStack 1.2.0 - Redis Stack (RediSearch, vector search)
- Microsoft.Playwright 1.57.0 - Headless browser automation
- OpenAI SDK (via Microsoft.Extensions.AI.OpenAI) - OpenRouter API compatibility

**Infrastructure:**
- Polly 8.6.5 / Polly.Extensions.Http 3.0.0 - Resilience and retry policies
- FluentResults 4.0.0 - Result pattern for error handling
- SmartReader 0.11.0 - Article content extraction
- Markdig 0.44.0 - Markdown rendering (WebChat.Client)
- System.CommandLine 2.0.2 - CLI argument parsing

**Utilities:**
- JetBrains.Annotations 2025.2.4 - Code analysis annotations
- Microsoft.Extensions.Caching.Memory 10.0.2 - In-memory caching
- Microsoft.Extensions.Http 10.0.2 - HttpClient factory

## Configuration

**Environment:**
- Configuration via `appsettings.json` in each project
- User Secrets for local development (multiple UserSecretsIds across projects)
- Docker `.env` file for container environment variables
- `IOptions<T>` pattern for typed configuration

**Key Configuration Files:**
- `Agent/appsettings.json`: Main agent config (OpenRouter, Telegram, Redis, agent definitions)
- `McpServerLibrary/appsettings.json`: Jackett, qBittorrent settings
- `McpServerWebSearch/appsettings.json`: Brave Search, CapSolver API keys
- `McpServerMemory/appsettings.json`: Redis connection, OpenRouter embeddings
- `McpServerIdealista/appsettings.json`: Idealista API credentials

**Required Environment Variables (Docker):**
```
OPENROUTER_APIKEY        # OpenRouter API key
TELEGRAM_BOTTOKEN        # Telegram bot tokens (per agent)
REDIS_CONNECTIONSTRING   # Redis connection string
BRAVE_APIKEY             # Brave Search API key
CAPSOLVER_APIKEY         # CapSolver CAPTCHA service (optional)
JACKETT_APIKEY           # Jackett torrent search
QBITTORRENT_USER         # qBittorrent credentials
QBITTORRENT_PASSWORD     # qBittorrent credentials
IDEALISTA_APIKEY         # Idealista real estate API
IDEALISTA_APISECRET      # Idealista OAuth secret
```

## Platform Requirements

**Development:**
- .NET 10 SDK
- Docker Desktop (for integration tests with Testcontainers)
- Redis (local or Docker) for state persistence testing

**Production:**
- Docker with Docker Compose
- Linux containers (specified in Dockerfiles: `DockerDefaultTargetOS=Linux`)
- Redis Stack server (for vector search in memory system)
- External services: OpenRouter, Telegram, Brave Search, qBittorrent, Jackett, Plex

## Project SDKs

| Project | SDK |
|---------|-----|
| Agent | Microsoft.NET.Sdk.Web |
| Domain | Microsoft.NET.Sdk |
| Infrastructure | Microsoft.NET.Sdk |
| McpServer* | Microsoft.NET.Sdk (Exe) |
| WebChat | Microsoft.NET.Sdk.Web |
| WebChat.Client | Microsoft.NET.Sdk.BlazorWebAssembly |
| Tests | Microsoft.NET.Sdk |

---

*Stack analysis: 2026-01-19*
