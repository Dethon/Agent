using System.CommandLine;
using Agent.Settings;
using Domain.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Modules;

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
            .AddOpenRouterAdapter(settings)
            .AddAgentFactory(settings)
            .AddChatMonitoring(settings, cmdParams)
            .AddSingleton<AgentResolver>();
    }

    public static CommandLineParams GetCommandLineParams(string[] args)
    {
        var workersOption = new Option<int>(name: "--workers", aliases: ["-w"])
        {
            Description = "Number of workers to run in daemon mode",
            Required = false,
            DefaultValueFactory = _ => 10
        };
        var rootCommand = new RootCommand("Agent Application")
        {
            workersOption
        };

        var parseResult = rootCommand.Parse(args);
        parseResult.Invoke();
        parseResult.ThrowIfErrors();
        parseResult.ThrowIfSpecialOption();
        return new CommandLineParams
        {
            WorkersCount = parseResult.GetValue(workersOption),
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