using Agent.App;
using Agent.Modules;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
var cmdParams = ConfigModule.GetCommandLineParams(args);
builder.Services.ConfigureJack(settings, cmdParams);

using var host = builder.Build();
await host.StartAgent(cmdParams);
