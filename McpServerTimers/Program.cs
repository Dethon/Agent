using Mcp.Hosting;
using McpServerTimers.Modules;
using McpServerTimers.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<TimerSettings>();
builder.Services.ConfigureTimers(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();