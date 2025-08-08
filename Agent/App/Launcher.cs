using Agent.Settings;
using Microsoft.Extensions.Hosting;

namespace Agent.App;

public static class Launcher
{
    public static async Task StartAgent(this IHost host, AgentSettings settings, CommandLineParams cmdParams)
    {
        var action = ResolveAction(settings, cmdParams);
        await host.StartAsync();
        await action(host.Services);
        await host.StopAsync();
    }

    private static Func<IServiceProvider, Task> ResolveAction(AgentSettings settings, CommandLineParams cmdParams)
    {
        
        if(cmdParams.Prompt is not null)
        {
            return services => Command.Start(services, cmdParams.Prompt, settings);
        }
        if (cmdParams.IsDaemon)
        {
            return Monitoring.Start;
        } 
        throw new ArgumentException("Invalid command line parameters. Please provide a prompt or run in daemon mode.");
    }
}