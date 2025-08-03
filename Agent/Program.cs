using Agent.App;
using Agent.Modules;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var cmdParams = ConfigModule.GetCommandLineParams(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureJack(settings, cmdParams);

using var host = builder.Build();
await host.StartAgent(cmdParams);
