using Mcp.Hosting;
using McpServerPrinter.Modules;
using McpServerPrinter.Settings;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.BindSettings<PrinterSettings>();
builder.Services.ConfigurePrinter(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();