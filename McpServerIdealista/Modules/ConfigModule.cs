using Domain.Contracts;
using Infrastructure.Clients;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using McpServerIdealista.McpPrompts;
using McpServerIdealista.McpTools;
using McpServerIdealista.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerIdealista.Modules;

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
                .AddIdealistaClient(settings)
                .AddMcpServer()
                .WithHttpTransport()
                .AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    try
                    {
                        return await next(context, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                        logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                        return ToolResponse.Create(ex);
                    }
                })
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