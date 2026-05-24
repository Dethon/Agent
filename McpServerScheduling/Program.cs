using McpServerScheduling.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureScheduling(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();