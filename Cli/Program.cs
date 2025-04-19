using Cli.Modules;
using Domain.Agents;
using Domain.Tools;
using Infrastructure.ToolAdapters.FileDownloadTools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services
    .AddOpenRouterAdapter(settings)
    .AddJacketTool(settings)
    .AddTransient<AgentResolver>()
    .AddTransient<FileDownloadTool, QBittorrentDownloadAdapter>();

using var host = builder.Build();
await host.StartAsync();

// Application logic start
if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
{
    Console.WriteLine("Usage: download-agent [prompt]");
    Console.WriteLine("Example: download-agent \"frozen 2 movie in english\"");
    return;
}

var prompt = string.Join(' ', args);

var agentResolver = host.Services.GetRequiredService<AgentResolver>();
var agent = agentResolver.Resolve(AgentType.Download);
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var responses = await agent.Run(prompt, lifetime.ApplicationStopping);
Console.WriteLine(string.Join('\n', responses.Select(r => r.Content)));
// Application logic end

await host.StopAsync();