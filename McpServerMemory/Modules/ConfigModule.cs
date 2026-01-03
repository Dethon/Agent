using System.Net.Http.Headers;
using Domain.Contracts;
using Infrastructure.Memory;
using McpServerMemory.McpPrompts;
using McpServerMemory.McpTools;
using McpServerMemory.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace McpServerMemory.Modules;

public static class ConfigModule
{
    public static McpSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<McpSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        // Redis
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(settings.RedisConnectionString));
        services.AddSingleton<IMemoryStore, RedisStackMemoryStore>();

        // Embedding service
        services.AddHttpClient<IEmbeddingService, OpenRouterEmbeddingService>((httpClient, _) =>
        {
            httpClient.BaseAddress = new Uri(settings.OpenRouter.BaseUrl);
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.OpenRouter.ApiKey);
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            return new OpenRouterEmbeddingService(httpClient, settings.Embedding.Model);
        });

        // MCP Server
        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpMemoryStoreTool>()
            .WithTools<McpMemoryRecallTool>()
            .WithTools<McpMemoryForgetTool>()
            .WithTools<McpMemoryReflectTool>()
            .WithTools<McpMemoryListTool>()
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}