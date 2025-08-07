using Agent.App;
using Agent.Modules;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var cmdParams = ConfigModule.GetCommandLineParams(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureJack(settings, cmdParams);

if(builder.Environment.IsDevelopment())
{
    cmdParams = cmdParams with
    {
        IsDaemon = false,
        Prompt = "squid game season 1 download all episodes"
    };
}

using var host = builder.Build();
await host.StartAgent(cmdParams);
