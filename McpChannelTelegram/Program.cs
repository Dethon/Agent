using McpChannelTelegram.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureChannel(settings);

var app = builder.Build();
app.MapMcp();

await app.RunAsync();
