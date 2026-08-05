using Mcp.Hosting;
using McpChannelServiceBus.Modules;
using McpChannelServiceBus.Settings;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<ChannelSettings>();
builder.Services.ConfigureChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();