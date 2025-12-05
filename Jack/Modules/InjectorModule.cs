using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients;
using Jack.App;
using Jack.Settings;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace Jack.Modules;

public static class InjectorModule
{
    private const string AgentName = "jack";

    private const string AgentDescription = """
                                            Autonomous media acquisition and library management agent. Operates as 'Captain Jack' - a pirate-themed AI that handles the complete lifecycle of media requests without requiring step-by-step confirmation.

                                            WHEN TO USE THIS AGENT:
                                            - User wants to download a specific movie, TV show, or other media content
                                            - User needs to check download status or progress
                                            - User wants to organize or move media files in their library
                                            - User wants to cancel active downloads

                                            HOW TO INTERACT:
                                            - For specific titles: Simply pass the title (e.g., 'The Matrix', 'Breaking Bad S01E01'). The agent will autonomously search, select the best quality version (1080p+, high seeders, no HDR), download it, organize it into the library, and report back.
                                            - For ambiguous titles: The agent will ask for clarification (e.g., 'Avatar' could be 2009 or 2022).
                                            - For vague requests: The agent will provide 3-5 recommendations (e.g., 'a good horror movie').
                                            - For status: Ask for 'status' or 'progress' to get a report on all active downloads.
                                            - For cancellation: Say 'cancel' or 'stop' to abort all active downloads and clean up.

                                            AUTONOMOUS WORKFLOW (no confirmation needed):
                                            1. Searches multiple torrent indexers with varied query strings
                                            2. Selects optimal result based on quality, seeders, and file size
                                            3. Initiates download immediately
                                            4. Organizes completed downloads into the library structure
                                            5. Cleans up temporary files and tasks

                                            RESPONSE STYLE: Pirate-themed, witty, and concise. Speaks like Captain Jack Sparrow.
                                            """;

    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings)
        {
            var mcpEndpoints = settings.McpServers.Select(x => x.Endpoint).ToArray();
            return services
                .AddSingleton<Func<CancellationToken, Task<AIAgent>>>(sp => ct =>
                    McpAgent.CreateAsync(
                        mcpEndpoints,
                        sp.GetRequiredService<IChatClient>(),
                        AgentName,
                        AgentDescription,
                        ct))
                .AddSingleton<ThreadResolver>()
                .AddSingleton<CancellationResolver>()
                .AddOpenRouterAdapter(settings);
        }

        public IServiceCollection AddChatMonitoring(AgentSettings settings, CommandLineParams cmdParams)
        {
            services = services
                .AddSingleton<TaskQueue>(_ => new TaskQueue(cmdParams.WorkersCount * 2))
                .AddSingleton<ChatMonitor>()
                .AddSingleton<AgentCleanupMonitor>()
                .AddHostedService<ChatMonitoring>()
                .AddHostedService<CleanupMonitoring>()
                .AddWorkers(cmdParams);

            return cmdParams.ChatInterface switch
            {
                ChatInterface.Cli => services.AddSingleton<IChatMessengerClient, CliChatMessengerClient>(_ =>
                    new CliChatMessengerClient("Jack")),
                ChatInterface.Telegram =>
                    services.AddSingleton<IChatMessengerClient, TelegramBotChatMessengerClient>(_ =>
                    {
                        var botClient = new TelegramBotClient(settings.Telegram.BotToken);
                        return new TelegramBotChatMessengerClient(botClient, settings.Telegram.AllowedUserNames);
                    }),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(cmdParams.ChatInterface), "Unsupported chat interface")
            };
        }

        private IServiceCollection AddWorkers(CommandLineParams cmdParams)
        {
            for (var i = 0; i < cmdParams.WorkersCount; i++)
            {
                services.AddSingleton<IHostedService, TaskRunner>();
            }

            return services;
        }

        private IServiceCollection AddOpenRouterAdapter(AgentSettings settings)
        {
            return services.AddSingleton<IChatClient>(_ =>
                new OpenAiClient(settings.OpenRouter.ApiUrl, settings.OpenRouter.ApiKey, settings.OpenRouter.Models));
        }
    }
}