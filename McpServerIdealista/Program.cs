using Mcp.Hosting;
using McpServerIdealista.Modules;
using McpServerIdealista.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<McpSettings>();
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();