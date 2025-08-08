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
        Prompt = "oathbringer epub, download 2 different alternatives"
    };
}

using var host = builder.Build();
await host.StartAgent(settings, cmdParams);
