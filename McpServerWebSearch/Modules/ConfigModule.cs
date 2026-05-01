using Domain.Contracts;
using Infrastructure.Clients;
using Infrastructure.Clients.Browser;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using McpServerWebSearch.McpPrompts;
using McpServerWebSearch.McpTools;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerWebSearch.Modules;

public static class ConfigModule
{
    private const string SessionIdHeader = "Mcp-Session-Id";

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
            CapSolver = config.GetSection("CapSolver").Get<CapSolverConfiguration>(),
            Camoufox = config.GetSection("Camoufox").Get<CamoufoxConfiguration>()
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
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
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
                }))
                .WithTools<McpWebSearchTool>()
                .WithTools<McpWebBrowseTool>()
                .WithTools<McpWebSnapshotTool>()
                .WithTools<McpWebActionTool>()
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
                return new PlaywrightWebBrowser(captchaSolver, settings.Camoufox?.WsEndpoint);
            });

            return services;
        }
    }

    extension(IApplicationBuilder app)
    {
        // Closes the browser session for an MCP session when the client sends DELETE /mcp
        // (the standard graceful-disconnect signal in the Streamable HTTP transport).
        // Idle/abandoned sessions are reclaimed by BrowserSessionManager's prune timer.
        public IApplicationBuilder UseBrowserSessionCleanupOnMcpDelete(string mcpPath)
        {
            return app.Use(async (context, next) =>
            {
                var isMcpDelete = HttpMethods.IsDelete(context.Request.Method)
                    && context.Request.Path.StartsWithSegments(mcpPath)
                    && context.Request.Headers.TryGetValue(SessionIdHeader, out var sessionIdHeader)
                    && !string.IsNullOrEmpty(sessionIdHeader.ToString());

                await next();

                if (!isMcpDelete)
                {
                    return;
                }

                var sessionId = context.Request.Headers[SessionIdHeader].ToString();
                try
                {
                    var browser = context.RequestServices.GetRequiredService<IWebBrowser>();
                    await browser.CloseSessionAsync(sessionId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    context.RequestServices.GetService<ILogger<Program>>()?
                        .LogWarning(ex, "Failed to close browser session {SessionId} on MCP DELETE", sessionId);
                }
            });
        }
    }
}
