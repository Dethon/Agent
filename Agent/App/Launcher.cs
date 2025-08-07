using Agent.Settings;
using Microsoft.Extensions.Hosting;

namespace Agent.App;

public static class Launcher
{
    public static async Task StartAgent(this IHost host, CommandLineParams cmdParams)
    {
        var action = ResolveAction(cmdParams);
        await host.StartAsync();
        await action(host.Services);
        await host.StopAsync();
    }

    private static Func<IServiceProvider, Task> ResolveAction(CommandLineParams cmdParams)
    {
        
        if(cmdParams.Prompt is not null)
        {
            return services => Command.Start(services, cmdParams.Prompt);
        }
        if (cmdParams.IsDaemon)
        {
            return Monitoring.Start;
        } 
        throw new ArgumentException("Invalid command line parameters. Please provide a prompt or run in daemon mode.");
    }
}