using Cli.App;
using Cli.Modules;
using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length == 0 || args.Any(x => x is "--help" or "-h"))
{
    Console.WriteLine("Usage: download-agent [options] [prompt]");
    Console.WriteLine("Options:");
    Console.WriteLine("-h, --help: Shows help information");
    Console.WriteLine("-d: Runs in daemon mode listening to telegram messages");
    Console.WriteLine("--ssh: Uses ssh to access downloaded files");
    return;
}

var sshMode = args.Contains("--ssh");
var isDaemon = args.Contains("-d");

if (sshMode)
{
    Console.WriteLine("SSH mode enabled.");
}

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services
    .AddMemoryCache()
    .AddOpenRouterAdapter(settings)
    .AddJacketClient(settings)
    .AddQBittorrentClient(settings)
    .AddFileSystemClient(settings, sshMode)
    .AddChatMonitoring(settings)
    .AddAttachments()
    .AddTools(settings)
    .AddSingleton<IAgentResolver, AgentResolver>();

if (isDaemon)
{
    builder.Services.AddWorkers(10);
    using var host = builder.Build();

    await host.StartAsync();
    await Monitoring.Start(host.Services);
    await host.StopAsync();
}
else
{
    using var host = builder.Build();

    await host.StartAsync();
    var prompt = args[^1];
    await Command.Start(host.Services, prompt);
    await host.StopAsync();
}