using McpServerWebSearch.Modules;
using Microsoft.AspNetCore.Builder;

const string mcpPath = "/mcp";

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.UseBrowserSessionCleanupOnMcpDelete(mcpPath);
app.MapMcp(mcpPath);

await app.RunAsync();
