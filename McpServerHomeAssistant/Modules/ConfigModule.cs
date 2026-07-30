using Domain.Contracts;
using Domain.Prompts;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using McpServerHomeAssistant.McpPrompts;
using McpServerHomeAssistant.McpResources;
using McpServerHomeAssistant.McpTools;
using McpServerHomeAssistant.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerHomeAssistant.Modules;

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
            // The podcast-episode action is advertised only when Music Assistant is reachable, so a
            // deployment without it never lists an action that cannot work.
            var music = settings.MusicAssistant;
            var musicConfigured = music?.IsConfigured == true;
            if (musicConfigured)
            {
                services.AddMusicAssistantClient(music!.BaseUrl, music.Token);
            }

            services
                .AddSingleton(settings)
                .AddHomeAssistantClient(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.Token)
                .AddSingleton(sp => new HaCatalogProvider(
                    sp.GetRequiredService<IHomeAssistantClient>,
                    extraServices: musicConfigured ? [HaMusicActions.PodcastEpisodes] : null))
                .AddSingleton(sp => new HaFileSystem(
                    sp.GetRequiredService<HaCatalogProvider>(),
                    sp.GetRequiredService<IHomeAssistantClient>,
                    musicClientFactory: musicConfigured ? sp.GetRequiredService<IMusicAssistantClient> : null))
                .AddSingleton(sp => new HomeAssistantSetupSummary(sp.GetRequiredService<HaCatalogProvider>()))
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
                .WithTools<FsGlobTool>()
                .WithTools<FsInfoTool>()
                .WithTools<FsReadTool>()
                .WithTools<FsSearchTool>()
                .WithTools<FsExecTool>()
                .WithResources<FileSystemResource>()
                .WithPrompts<McpSystemPrompt>();

            return services;
        }
    }
}