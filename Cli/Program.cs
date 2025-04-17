using Cli.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Domain.Agents;

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services
    .AddOpenRouterAdapter(settings);

using var host = builder.Build();
await host.StartAsync();

// Application logic start
var agent = AgentResolver.Resolve(AgentType.Download);
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var response = await agent.Run("", lifetime.ApplicationStopping);
Console.WriteLine(response.Answer);
// Application logic end

await host.StopAsync();