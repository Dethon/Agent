using Domain.Contracts;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using McpServerWebSearch.McpTools;
using McpServerWebSearch.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerWebSearch.Modules;

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

    extension(IServiceCollection services)
    {
        public IServiceCollection ConfigureMcp(McpSettings settings)
        {
            services
                .AddSingleton(settings)
                .AddWebSearchClients(settings)
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<McpWebSearchTool>()
                .WithTools<McpWebFetchTool>();

            return services;
        }

        private IServiceCollection AddWebSearchClients(McpSettings settings)
        {
            services.AddHttpClient<IWebSearchClient, BraveSearchClient>((httpClient, _) =>
                {
                    httpClient.BaseAddress = new Uri(settings.BraveSearch.ApiUrl);
                    httpClient.Timeout = TimeSpan.FromSeconds(30);
                    return new BraveSearchClient(httpClient, settings.BraveSearch.ApiKey);
                })
                .AddRetryWithExponentialWaitPolicy(
                    attempts: 2,
                    waitTime: TimeSpan.FromSeconds(1),
                    attemptTimeout: TimeSpan.FromSeconds(15));

            services.AddHttpClient<IWebFetcher, WebContentFetcher>(httpClient =>
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(30);
                    return new WebContentFetcher(httpClient);
                })
                .AddRetryWithExponentialWaitPolicy(
                    attempts: 2,
                    waitTime: TimeSpan.FromSeconds(1),
                    attemptTimeout: TimeSpan.FromSeconds(20));

            return services;
        }
    }
}