using Cli.App;
using Cli.Modules;
using Domain.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length == 0 || args.Any(x => x is "--help" or "-h"))
{
    Console.WriteLine("Usage: download-agent [options] [prompt]");
    Console.WriteLine("Options:");
    Console.WriteLine("-h, --help: Shows help information");
    Console.WriteLine("-d: Runs in daemon mode listening to telegram messages");
    Console.WriteLine("--ssh: Uses ssh to access downloaded files");
    return;
}

var sshMode = args.Contains("--ssh");
var isDaemon = args.Contains("-d");
var prompt = args[^1];

if (sshMode)
{
    Console.WriteLine("SSH mode enabled.");
}

var builder = Host.CreateApplicationBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services
    .AddOpenRouterAdapter(settings)
    .AddJacketClient(settings)
    .AddQBittorrentClient(settings)
    .AddFileSystemClient(settings, sshMode)
    .AddAttachments()
    .AddTools(settings)
    .AddTransient<AgentResolver>();

if (isDaemon)
{
    builder.Services.AddSingleton<TaskQueue>();
    builder.Services.AddSingleton<TelegramMonitor>();
    builder.Services.AddHostedService<TaskRunner>();
    using var host = builder.Build();
    
    var telegramMonitor = host.Services.GetRequiredService<TelegramMonitor>();
    await host.StartAsync();
    await telegramMonitor.Monitor();
    await host.StopAsync();
}
else
{
    using var host = builder.Build();
    await host.StartAsync();
    await Command.Start(host.Services, prompt);
    await host.StopAsync();
}
