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

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services
    .AddOpenRouterAdapter(settings)
    .AddQBittorrentTool(settings)
    .AddJacketTool(settings)
    .AddFileManagingTools(settings, sshMode)
    .AddTransient<AgentResolver>();

using var host = builder.Build();
await host.StartAsync();

// Application logic start
var agentResolver = host.Services.GetRequiredService<AgentResolver>();
var agent = agentResolver.Resolve(AgentType.Download);
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
await agent.Run(prompt, lifetime.ApplicationStopping);
// Application logic end

await host.StopAsync();