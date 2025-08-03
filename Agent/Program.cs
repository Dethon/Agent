using Cli.App;
using Cli.Modules;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
var cmdParams = ConfigModule.GetCommandLineParams(args);
builder.Services.ConfigureJack(settings, cmdParams);

if (cmdParams.IsDaemon)
{
    builder.Services.AddWorkers(10);
    using var host = builder.Build();

    await host.StartAsync();
    await Monitoring.Start(host.Services);
    await host.StopAsync();
}
if(cmdParams.Prompt is not null)
{
    using var host = builder.Build();

    await host.StartAsync();
    await Command.Start(host.Services, cmdParams.Prompt);
    await host.StopAsync();
}