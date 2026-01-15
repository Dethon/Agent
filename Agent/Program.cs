using Agent.Hubs;
using Agent.Modules;
using Agent.Settings;

var builder = WebApplication.CreateBuilder(args);
var cmdParams = ConfigModule.GetCommandLineParams(args);
var settings = builder.Configuration.GetSettings();

if (cmdParams.ChatInterface == ChatInterface.Web)
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.SetIsOriginAllowed(_ => true)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        });
    });
}

builder.Services.ConfigureAgents(settings, cmdParams);

var app = builder.Build();

if (cmdParams.ChatInterface == ChatInterface.Web)
{
    app.UseCors();
    app.MapHub<ChatHub>("/hubs/chat");
}

await app.RunAsync();