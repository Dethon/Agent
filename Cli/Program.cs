using Cli.Modules;
using Domain.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length == 0 || args.Any(x => x is "--help" or "-h"))
{
    Console.WriteLine("Usage: download-agent [options] [prompt]");
    Console.WriteLine("Options:");
    Console.WriteLine("-h, --help: Shows help information");
    Console.WriteLine("--ssh: Uses ssh to access downloaded files");
    return;
}

var sshMode = args.Contains("--ssh");
var prompt = args[^1];

if (sshMode)
{
    Console.WriteLine("SSH mode enabled.");
}

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services
    .AddOpenRouterAdapter(settings)
    .AddJacketClient(settings)
    .AddQBittorrentClient(settings)
    .AddFileSystemClient(settings, sshMode)
    .AddAttachments()
    .AddTools(settings)
    .AddTransient<AgentResolver>();

using var host = builder.Build();
await host.StartAsync();

// Application logic start
var scope = host.Services.CreateAsyncScope();
var agentResolver = scope.ServiceProvider.GetRequiredService<AgentResolver>();
var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();

var agent = agentResolver.Resolve(AgentType.Download);
await agent.Run(prompt, lifetime.ApplicationStopping);
// Application logic end

await host.StopAsync();