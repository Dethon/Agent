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
var agentResolver = host.Services.GetRequiredService<AgentResolver>();
var agent = agentResolver.Resolve(AgentType.Download);
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var responses = await agent.Run("frozen 2 movie in english", lifetime.ApplicationStopping);
Console.WriteLine(string.Join('\n', responses.Select(r => r.Content)));
// Application logic end

await host.StopAsync();