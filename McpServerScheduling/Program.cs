using Mcp.Hosting;
using McpServerScheduling.Modules;
using McpServerScheduling.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<SchedulingSettings>();
builder.Services.ConfigureScheduling(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();