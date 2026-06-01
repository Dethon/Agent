using McpServerPrinter.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigurePrinter(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();