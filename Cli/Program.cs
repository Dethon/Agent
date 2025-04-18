using Cli.Modules;
using Domain.Agents;
using Domain.Tools;
using Infrastructure.ToolAdapters.FileDownloadTools;
using Infrastructure.ToolAdapters.FileSearchTools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services
    .AddOpenRouterAdapter(settings)
    .AddTransient<AgentResolver>()
    .AddTransient<FileDownloadTool, QBittorrentDownloadAdapter>()
    .AddTransient<FileSearchTool, JackettSearchAdapter>();

using var host = builder.Build();
await host.StartAsync();

// Application logic start
var agentResolver = host.Services.GetRequiredService<AgentResolver>();
var agent = agentResolver.Resolve(AgentType.Download);
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var responses = await agent.Run("Download t-rex origami diagrams", lifetime.ApplicationStopping);
Console.WriteLine(string.Join('\n', responses.Select(r => r.Content)));
// Application logic end

await host.StopAsync();