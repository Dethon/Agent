using Agent.Modules;
using Agent.Settings;

var builder = WebApplication.CreateBuilder(args);
var cmdParams = ConfigModule.GetCommandLineParams(args);
var settings = builder.Configuration.GetSettings();

builder.Services.ConfigureAgents(settings, cmdParams);

var app = builder.Build();

await app.RunAsync();
