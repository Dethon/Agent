using McpChannelSignalR.Hubs;
using McpChannelSignalR.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureChannel(settings);

var app = builder.Build();
app.MapHub<ChatHub>("/hubs/chat");
app.MapMcp();

await app.RunAsync();
