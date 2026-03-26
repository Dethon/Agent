using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureChannel(settings);
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

var app = builder.Build();
app.UseCors();
app.MapHub<ChatHub>("/hubs/chat");
app.MapMcp();

await app.RunAsync();