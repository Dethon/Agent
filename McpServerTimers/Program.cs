using McpServerTimers.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureTimers(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();