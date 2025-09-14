using McpServer.CommandRunner.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
await builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.MapMcp();

await app.RunAsync();