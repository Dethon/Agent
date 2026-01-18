using Domain.Contracts;
using Infrastructure.Clients;
using Infrastructure.Clients.Browser;
using Infrastructure.Extensions;
using McpServerWebSearch.McpPrompts;
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
        if (settings == null)
        {
            throw new InvalidOperationException("Settings not found");
        }

        // Bind nested sections explicitly for environment variable support
        settings = settings with
        {
            CapSolver = config.GetSection("CapSolver").Get<CapSolverConfiguration>()
        };

        return settings;
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
                .WithTools<McpWebBrowseTool>()
                .WithTools<McpWebClickTool>()
                .WithTools<McpWebInspectTool>()
                .WithPrompts<McpSystemPrompt>();

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

            // Register CapSolver if configured
            if (!string.IsNullOrEmpty(settings.CapSolver?.ApiKey))
            {
                services.AddHttpClient<ICaptchaSolver, CapSolverClient>((httpClient, _) =>
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(3);
                    return new CapSolverClient(httpClient, settings.CapSolver.ApiKey);
                });
            }

            // Register browser for session-based browsing with modal dismissal and captcha solving
            services.AddSingleton<IWebBrowser>(sp =>
            {
                var captchaSolver = sp.GetService<ICaptchaSolver>();
                return new PlaywrightWebBrowser(captchaSolver);
            });

            return services;
        }
    }
}