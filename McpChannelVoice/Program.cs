using McpChannelVoice.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetVoiceSettings();
builder.Services.ConfigureVoiceChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();