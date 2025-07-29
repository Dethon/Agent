using Cli.App;
using Cli.Modules;
using Domain.Agents;
using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Any(x => x is "--help" or "-h"))
{
    Console.WriteLine("Usage: download-agent [options] [prompt]");
    Console.WriteLine("Options:");
    Console.WriteLine("-h, --help: Shows help information");
    Console.WriteLine("-p <prompt>: Runs a prompt in one shot mode");
    Console.WriteLine("--ssh: Uses ssh to access downloaded files");
    return;
}

var sshMode = args.Contains("--ssh");
var promptIndex = Array.IndexOf(args, "-p");
var prompt = promptIndex != -1 && promptIndex < args.Length - 1 ? args[promptIndex + 1] : null;
var isDaemon = args.Length == 0 || promptIndex == -1;
if (!isDaemon && prompt is null or "--ssh")
{
    Console.WriteLine($"Error: Prompt argument is invalid {prompt}");
    return;
}

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
    .AddTransient<IAgentResolver, AgentResolver>();

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
    await Command.Start(host.Services, prompt);
    await host.StopAsync();
}