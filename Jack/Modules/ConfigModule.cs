using System.CommandLine;
using Jack.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jack.Modules;

public static class ConfigModule
{
    public static AgentSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<AgentSettings>();
        if (settings == null)
        {
            throw new InvalidOperationException("Settings not found");
        }

        return settings;
    }

    public static IServiceCollection ConfigureJack(
        this IServiceCollection services, AgentSettings settings, CommandLineParams cmdParams)
    {
        return services
            .AddAgent(settings)
            .AddChatMonitoring(settings, cmdParams);
    }

    public static CommandLineParams GetCommandLineParams(string[] args)
    {
        var workersOption = new Option<int>(name: "--workers", aliases: ["-w"])
        {
            Description = "Number of workers to run in daemon mode",
            Required = false,
            DefaultValueFactory = _ => 10
        };
        var chatOption = new Option<ChatInterface>("--chat")
        {
            Description = "Chat interface to interact with the agent",
            Required = false,
            DefaultValueFactory = _ => ChatInterface.Telegram
        };
        var rootCommand = new RootCommand("Jack Application")
        {
            workersOption,
            chatOption
        };

        var parseResult = rootCommand.Parse(args);
        parseResult.Invoke();
        parseResult.ThrowIfErrors();
        parseResult.ThrowIfSpecialOption();
        return new CommandLineParams
        {
            WorkersCount = parseResult.GetValue(workersOption),
            ChatInterface = parseResult.GetValue(chatOption)
        };
    }

    private static void ThrowIfErrors(this ParseResult parseResult)
    {
        if (parseResult.Errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid command line arguments");
        }
    }

    private static void ThrowIfSpecialOption(this ParseResult parseResult)
    {
        var helpOption = parseResult.GetValue<bool>("--help");
        var versionOption = parseResult.GetValue<bool>("--version");
        if (helpOption || versionOption)
        {
            throw new InvalidOperationException("Invalid command line arguments");
        }
    }
}