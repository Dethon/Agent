using System.CommandLine;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.Caching.Memory;
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
            .AddWorkers(cmdParams.WorkersCount)
            .AddMemoryCache()
            .AddOpenRouterAdapter(settings)
            .AddChatMonitoring(settings)
            .AddTransient<DownloaderPrompt>()
            .AddTransient<AgentResolver>(sp => new AgentResolver(
                sp.GetRequiredService<DownloaderPrompt>(),
                sp.GetRequiredService<ILargeLanguageModel>(),
                settings.McpServers.Select(x=> x.Endpoint).ToArray(),
                sp.GetRequiredService<IMemoryCache>()));
    }

    public static CommandLineParams GetCommandLineParams(string[] args)
    {var sshOption = new Option<bool>(name: "--ssh")
        {
            Description = "Use SSH to access downloaded files",
            Required = false,
            DefaultValueFactory = _ => false
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
            workersOption
        };
        
        var parseResult = rootCommand.Parse(args);
        parseResult.Invoke();
        parseResult.ThrowIfErrors();
        parseResult.ThrowIfSpecialOption();
        return new CommandLineParams
        {
            SshMode = parseResult.GetValue(sshOption),
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