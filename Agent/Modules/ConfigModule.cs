using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;

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
        if (cmdParams.IsDaemon)
        {
            services = services.AddWorkers(cmdParams.WorkersCount);
        }
        return services
            .AddMemoryCache()
            .AddOpenRouterAdapter(settings)
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient(settings, cmdParams.SshMode)
            .AddChatMonitoring(settings)
            .AddTools(settings)
            .AddTransient<IAgentResolver, AgentResolver>();
    }

    public static CommandLineParams GetCommandLineParams(string[] args)
    {var sshOption = new Option<bool>(name: "--ssh")
        {
            Description = "Use SSH to access downloaded files",
            Required = false,
            DefaultValueFactory = _ => false
        };
        var promptOption = new Option<string?>(name: "--prompt", aliases: ["-p"])
        {
            Description = "Run a prompt in one shot mode",
            Required = false,
            DefaultValueFactory = _ => null
        };
        var workersOption = new Option<int>(name: "--workers", aliases: ["-w"])
        {
            Description = "Number of workers to run in daemon mode",
            Required = false,
            DefaultValueFactory = _ => 10
        };
        var rootCommand = new RootCommand("Agent Application")
        {
            sshOption,
            promptOption,
            workersOption
        };
        
        var parseResult = rootCommand.Parse(args);
        parseResult.Invoke();
        parseResult.ThrowIfErrors();
        parseResult.ThrowIfSpecialOption();
        return new CommandLineParams
        {
            IsDaemon = parseResult.GetValue(promptOption) is null,
            SshMode = parseResult.GetValue(sshOption),
            Prompt = parseResult.GetValue(promptOption),
            WorkersCount = parseResult.GetValue(workersOption),
        };
    }

    private static void ThrowIfErrors(this ParseResult parseResult)
    {
        if(parseResult.Errors.Count > 0)
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