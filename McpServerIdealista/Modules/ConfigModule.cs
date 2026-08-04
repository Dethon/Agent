using Domain.Contracts;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerIdealista.McpPrompts;
using McpServerIdealista.McpTools;
using McpServerIdealista.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerIdealista.Modules;

public static class ConfigModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection ConfigureMcp(McpSettings settings)
        {
            services
                .AddIdealistaClient(settings)
                .AddToolServer(settings, ToolResponse.Create)
                .WithTools<McpPropertySearchTool>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }

        private IServiceCollection AddIdealistaClient(McpSettings settings)
        {
            services.AddHttpClient<IIdealistaClient, IdealistaClient>((httpClient, _) =>
                {
                    httpClient.BaseAddress = new Uri(settings.Idealista.ApiUrl);
                    httpClient.Timeout = TimeSpan.FromSeconds(30);
                    return new IdealistaClient(httpClient, settings.Idealista.ApiKey, settings.Idealista.ApiSecret);
                })
                .AddRetryWithExponentialWaitPolicy(
                    attempts: 2,
                    waitTime: TimeSpan.FromSeconds(1),
                    attemptTimeout: TimeSpan.FromSeconds(15));

            return services;
        }
    }
}